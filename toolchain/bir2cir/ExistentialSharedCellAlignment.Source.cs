using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ExistentialSharedCellAlignment
{
    internal static void ApplyAll(IReadOnlyList<BirFileDeclaration> roots,
        IReadOnlyList<SharedSyntheticSynthesis.CellStorage> cells,
        IReadOnlyDictionary<string, string> localCarriers, ReferenceMetadataIndex refs)
    {
        var slots = cells.Distinct().ToDictionary(cell => cell.Owner, StringComparer.Ordinal);
        var definitions = new Dictionary<string, List<IBirTypeDeclaration>>(StringComparer.Ordinal);
        foreach (var root in roots)
        foreach (var type in root.Types.OrEmpty())
            if (type.Name.HasValue && slots.ContainsKey(type.Name.Value))
            {
                if (!definitions.TryGetValue(type.Name.Value, out var declarations))
                    definitions[type.Name.Value] = declarations = new();
                declarations.Add(type);
            }
        // This is the current physical reachability graph, including carrier
        // declarations just emitted by synthesis, not a new source catalog.
        var types = SupertypeGraph.CollectSource(roots);
        var carriers = localCarriers.Values.ToHashSet(StringComparer.Ordinal);
        var projected = new Dictionary<string, TypeNode>(StringComparer.Ordinal);
        foreach (var root in roots) Walk(root, FindWrite);
        foreach (var (owner, physical) in projected)
        {
            var slot = slots[owner];
            foreach (var definition in definitions[owner])
            {
                var field = definition.Fields.Value.Single(field => field.Name.OrDefault() == slot.Field);
                field.Type = BirGraphValue.CopyType(physical);
                var parameter = definition.Ctors.Value.Single().Params.Value[slot.ConstructorParameter];
                parameter.Type = BirGraphValue.CopyType(physical);
            }
        }
        foreach (var root in roots) Walk(root, AlignUse);

        void FindWrite(BirGraphValue node)
        {
            var (owner, fieldName, value) = node switch {
                BirSetField write => (write.OwnerType.OrDefault() as TypeNode.Fqn, write.Name.OrDefault(), write.Value.OrDefault()),
                BirNew construction => (construction.Type.OrDefault() as TypeNode.Fqn, null, (IBirExpression)null),
                _ => (null, null, (IBirExpression)null),
            };
            if (owner == null || !slots.TryGetValue(owner.Name, out var slot)
                || node is BirSetField && fieldName != slot.Field
                || !definitions.TryGetValue(owner.Name, out var declarations)
                || !declarations[0].Fields.HasValue) return;
            var field = declarations[0].Fields.Value.Single(field => field.Name.OrDefault() == slot.Field);
            if (field._nativeLocation.OrDefault()) return;
            var declared = field.Type.OrDefault();
            if (node is BirNew construction) value = construction.Args.Value[slot.ConstructorParameter];
            if (Core(declared) is not TypeNode.Fqn { Args: { Length: > 0 } } logical
                || Core(BirNodeType.Of(value as BirGraphNode)) is not TypeNode.Fqn physical
                || !(carriers.Contains(physical.Name) || refs.IsExistentialPhysicalOwner(physical.Name))) return;
            var carrier = localCarriers.GetValueOrDefault(logical.Name);
            if (carrier == null) refs.TryExistentialPhysicalOwner(logical.Name, out carrier);
            if (carrier != null && SupertypeGraph.Reaches(physical, new TypeNode.Fqn(carrier), types, refs))
                projected[owner.Name] = declared switch {
                    TypeNode.Nullable => new TypeNode.Nullable(new TypeNode.Fqn(carrier)),
                    TypeNode.Oblivious => new TypeNode.Oblivious(new TypeNode.Fqn(carrier)),
                    _ => new TypeNode.Fqn(carrier),
                };
        }

        void AlignUse(BirGraphValue node)
        {
            var owner = node switch {
                BirNew construction => construction.Type.OrDefault() as TypeNode.Fqn,
                BirFieldGet read => read.OwnerType.OrDefault() as TypeNode.Fqn,
                BirSetField write => write.OwnerType.OrDefault() as TypeNode.Fqn,
                _ => null,
            };
            if (owner == null || !projected.TryGetValue(owner.Name, out var physical)) return;
            var slot = slots[owner.Name];
            if (node is BirFieldGet read && read.Name.OrDefault() == slot.Field)
            {
                read.Sty = BirGraphValue.CopyType(physical);
                if (read.MemberType.IsPresent && !read.MemberType.IsNull) read.MemberType = BirGraphValue.CopyType(physical);
            }
            if (node is BirSetField write && write.Name.OrDefault() == slot.Field
                && write.MemberType.IsPresent && !write.MemberType.IsNull)
                write.MemberType = BirGraphValue.CopyType(physical);
            if (node is BirNew construction)
            {
                if (construction.ArgTypes.HasValue) construction.ArgTypes.Value[slot.ConstructorParameter] = BirGraphValue.CopyType(physical);
                if (construction.MemberSignature.HasValue) construction.MemberSignature.Value[slot.ConstructorParameter] = BirGraphValue.CopyType(physical);
            }
        }
    }

    static void Walk(BirGraphValue node, Action<BirGraphValue> inspect)
    {
        if (node is IBirExecutablePayload) return;
        inspect(node);
        node.VisitChildren(child => Walk(child, inspect));
    }
}
