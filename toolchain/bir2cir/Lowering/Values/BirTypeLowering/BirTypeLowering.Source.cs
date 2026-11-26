using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class BirTypeLowering
{
    // One owning projection. The generated field profile selects physical slots;
    // generic-frame traversal alone is deliberately not this pass's domain.
    internal static void Apply(BirFileDeclaration root, PhysicalTypeContext context)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(context);
        Project(root, force: false);

        void Project(BirGraphValue node, bool force)
        {
            var declaration = node as IBirTypeDeclaration;
            var annotation = declaration?.Mods.OrDefault()?.Annotation.OrDefault() == true;
            var here = force || annotation || declaration?.Base.OrDefault() is TypeNode.Fqn attribute
                && attribute.Name.EndsWith("System.Attribute", StringComparison.Ordinal);
            if (node is BirNew construction)
            {
                if (!construction.Args.HasValue || !construction.ArgTypes.HasValue)
                    throw new InvalidDataException($"{context.File}: malformed current `new`: args and argTypes are required");
                if (construction.Args.Value.Length != construction.ArgTypes.Value.Length
                    || construction.ArgTypes.Value.Any(type => type == null))
                    throw new InvalidDataException($"{context.File}: malformed current `new`: argTypes must match args");
            }
            if (node is BirConvert conversion && (!conversion.E.HasValue || !conversion.To.HasValue))
                throw new InvalidDataException($"{context.File}: malformed current `conv`: e and to are required");

            // Birth the positional metadata from ORIGINAL scalar facts, before
            // physical rewriting destroys a suspend shape or a Nothing return.
            var metadata = CapturePhysicalMetadata(node);
            // The override markers are consumed here. A declaration ID names an open method,
            // not which of its constructed interface slots the frontend selected.
            if (node is BirFunctionDeclaration method && method.TypeParams.OrEmpty().Length != 0 && method.Overrides.HasValue)
                method.ConstructedOverrideOwners = method.Overrides.Value
                    .Where(edge => edge.DeclarationId.HasValue && edge.Owner.OrDefault() is TypeNode.Fqn { Args.Length: > 0 })
                    .Select(edge => (edge.PhysicalDeclarationId.OrDefault() ?? edge.DeclarationId.Value,
                        (TypeNode.Fqn)ProjectType(edge.Owner.Value, BirPhysicalTypeRole.Owner, here, context)))
                    .Distinct().ToArray();
            var unitValue = node is BirConstant constant && IsUnitHead(constant.Type.OrDefault())
                || node is BirTry expression && IsUnitHead(expression.Type.OrDefault());
            node.RewritePhysicalFieldsInPlace(
                (_, role, type) => ProjectType(type, role, here, context),
                (_, role, types) => ProjectTypes(types, role, here, context),
                (_, role, slot) => Project(slot.Read(), here || role == BirPhysicalTypeRole.Attributes));
            node.ConsumePhysicalSourceFacts();
            metadata();

            if (annotation)
            {
                declaration.Base = new TypeNode.Fqn("System.Attribute");
                foreach (var constructor in declaration.Ctors.OrEmpty())
                    if (!constructor.BaseArgs.HasValue && !constructor.ThisArgs.HasValue)
                        constructor.BaseArgs = Array.Empty<IBirExpression>();
                declaration.Mods.Value.Annotation = default;
            }
            if (unitValue)
            {
                if (node is BirConstant literal) literal.Type = VoidType;
                else ((BirTry)node).Type = VoidType;
            }
        }
    }

    static bool IsUnitHead(TypeNode type) => type is TypeNode.Fqn { Name: "kotlin.Unit" };

    static TypeNode[] ProjectTypes(TypeNode[] types, BirPhysicalTypeRole role, bool force,
        PhysicalTypeContext context)
    {
        if (role != BirPhysicalTypeRole.Interfaces || context.RefBuild && !force)
            return types.Select(type => type == null ? null : ProjectType(type, role, force, context)).ToArray();
        return types.SelectMany(type => type is TypeNode.Fqn owner
                && context.InterfaceMarkers.TryGetValue(owner.Name, out var markers)
                    ? markers.Select(marker => ProjectType(SupertypeGraph.SubstOwnerTvs(marker, owner.Args ?? []), BirPhysicalTypeRole.Owner, force, context))
                    : new[] { ProjectType(type, BirPhysicalTypeRole.Value, force, context) })
            .DistinctBy(type => SupertypeGraph.TypeKey((TypeNode.Fqn)type)).ToArray();
    }

    static TypeNode ProjectType(TypeNode type, BirPhysicalTypeRole role, bool force, PhysicalTypeContext context)
        => role switch
        {
            BirPhysicalTypeRole.Structural => ProjectTypeStructure(type, force, context),
            BirPhysicalTypeRole.Return => LowerSlot(type, context, PhysicalSlotRole.Return, force),
            BirPhysicalTypeRole.Owner => LowerSlot(type, context, PhysicalSlotRole.Owner, force),
            BirPhysicalTypeRole.Delegate => LowerSlot(type, context, PhysicalSlotRole.Delegate, force),
            BirPhysicalTypeRole.GenericArgument => LowerSlot(type, context, PhysicalSlotRole.GenericArgument, force),
            _ => LowerSlot(type, context, PhysicalSlotRole.Value, force),
        };

    // An unclassified structured Type is not an ordinary value slot. Preserve
    // its head/wrappers, but recurse over the old structural field effect: Fn
    // params/ret and Array.elem are physical slots even inside such a carrier.
    static TypeNode ProjectTypeStructure(TypeNode type, bool force, PhysicalTypeContext context)
    {
        TypeNode Structure(TypeNode value) => ProjectTypeStructure(value, force, context);
        TypeNode Value(TypeNode value) => LowerSlot(value, context, PhysicalSlotRole.Value, force);
        TypeNode Return(TypeNode value) => LowerSlot(value, context, PhysicalSlotRole.Return, force);
        return type switch
        {
            TypeNode.Fqn value => value with { Args = value.Args?.Select(Structure).ToArray() },
            TypeNode.Fn value => value with { Ret = Return(value.Ret), Params = value.Params.Select(Value).ToArray(),
                Recv = value.Recv is { } receiver ? Structure(receiver) : null,
                Ctx = value.Ctx?.Select(Structure).ToArray() },
            TypeNode.FnPtr value => value with { Ret = Return(value.Ret), Params = value.Params.Select(Value).ToArray() },
            TypeNode.Array value => value with { Elem = Value(value.Elem) },
            TypeNode.Nullable value => value with { Of = Structure(value.Of) },
            TypeNode.Oblivious value => value with { Of = Structure(value.Of) },
            TypeNode.Projection value => value with { Of = Structure(value.Of) },
            TypeNode.ByRef value => value with { Of = Structure(value.Of) },
            TypeNode.Ptr value => value with { Of = Structure(value.Of) },
            TypeNode.Mod value => value with { M = Structure(value.M), Of = Structure(value.Of) },
            _ => type,
        };
    }

    static Action CapturePhysicalMetadata(BirGraphValue node)
    {
        TypeNode Suspend(OptionalField<TypeNode> saved, TypeNode original) =>
            saved.HasValue ? BirGraphValue.CopyType(saved.Value)
            : original is TypeNode.Fn { Suspend: true } ? BirGraphValue.CopyType(original) : null;
        // These are the actual declaration metadata buyers. MethodImpls and
        // executable values do not acquire declaration-only positional facts.
        switch (node)
        {
            case BirParameter parameter:
            {
                var shape = Suspend(parameter.SuspendFnTypePre, parameter.Type.OrDefault());
                return () => { if (shape != null) parameter.SuspendFnType = shape; };
            }
            case BirFieldDeclaration field:
            {
                var shape = Suspend(field.SuspendFnTypePre, field.Type.OrDefault());
                return () => { if (shape != null) field.SuspendFnType = shape; };
            }
            case BirPropertyDeclaration property:
            {
                var shape = Suspend(property.SuspendFnTypePre, property.Type.OrDefault());
                return () => { if (shape != null) property.SuspendFnType = shape; };
            }
            case BirFunctionDeclaration method:
            {
                var shape = Suspend(method.RetSuspendFnTypePre, method.Ret.OrDefault());
                var nothing = method.Ret.OrDefault() is TypeNode.Fqn { Name: "kotlin.Nothing" };
                return () => {
                    if (shape != null) method.RetSuspendFnType = shape;
                    if (nothing) method.RetNothing = true;
                };
            }
            default: return () => { };
        }
    }
}
