using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinPropertyAccessors
{
    // The callback reports exact live Property rows before their owning edge is
    // removed. The pipeline, not this allocator, owns declaration retirement.
    internal static void AllocateAll(BirFileDeclaration file, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<MemberCallSubstitution.LocalPropertyAccessorKey,
            IReadOnlyList<MemberCallSubstitution.LocalPropertyAccessor>> localAccessors,
        Action<BirGraphValue, IReadOnlyList<BirPropertyDeclaration>> retireProperties)
    {
        ArgumentNullException.ThrowIfNull(retireProperties);
        void Walk(BirGraphValue node)
        {
            if (node == null || node is IBirExecutablePayload) return;
            switch (node)
            {
                case BirCallStatic call when call.Prop.OrDefault() is "get" or "set" && call.Method.HasValue:
                    // Selected static/companion metadata has already been
                    // consumed. Remaining static properties use the same
                    // forward spelling as their actual declarations.
                    PreserveCallIdentity(call, call.Method.Value, call.Prop.Value);
                    call.Method = PhysicalName(call.Method.Value, call.Prop.Value);
                    call.Prop = default;
                    break;
                case BirCallInstance call when call.Prop.OrDefault() is "get" or "set" && call.Method.HasValue:
                {
                    PreserveCallIdentity(call, call.Method.Value, call.Prop.Value);
                    var plan = AllocateCallName(call.Method.Value, call.Prop.Value, call.OwnerType.OrDefault(),
                        call.Args.OrEmpty().Length, call.Sig.OrDefault() ?? call.ArgTypes.OrDefault(),
                        call.TypeArgs.OrEmpty().Length, call.Super.OrDefault(), refs, localAccessors);
                    call.Method = plan.Name;
                    // Add a reference-proved virtual fact without erasing a
                    // prior hierarchy fact. Explicit super never adds one.
                    if (plan.Virtual) call.Virtual = true;
                    call.Prop = default;
                    break;
                }
                case BirConstrainedCall call when call.Prop.OrDefault() is "get" or "set" && call.Method.HasValue:
                {
                    ValidateCallIdentity(call.Method.Value, call.Prop.Value);
                    call.PropertyName = call.Method.Value; call.PropertyAccessor = call.Prop.Value;
                    var plan = AllocateCallName(call.Method.Value, call.Prop.Value, call.Iface.OrDefault(),
                        call.Args.OrEmpty().Length, call.Sig.OrDefault() ?? call.ArgTypes.OrDefault(),
                        call.TypeArgs.OrEmpty().Length, call.Super.OrDefault(), refs, localAccessors);
                    call.Method = plan.Name;
                    if (plan.Virtual) call.Virtual = true;
                    call.Prop = default;
                    break;
                }
            }
            var (methods, properties) = node switch {
                BirFileDeclaration owner => (owner.Methods.OrEmpty(), owner.Properties.OrEmpty()),
                IBirTypeDeclaration owner => (owner.Methods.OrEmpty(), owner.Properties.OrEmpty()),
                _ => (Array.Empty<BirFunctionDeclaration>(), Array.Empty<BirPropertyDeclaration>()),
            };
            foreach (var method in methods)
                // Preserve a narrower physical identity authored earlier.
                if (TryIdentity(method, out var name, out var kind) && method.Name.OrDefault() == name)
                    SetPhysicalName(method, PhysicalName(name, kind));
            var removed = new List<BirPropertyDeclaration>();
            foreach (var property in properties)
                if (!AllocateProperty(property, methods, stripIdentity: false)) removed.Add(property);
            if (removed.Count != 0)
            {
                retireProperties(node, removed);
                var survivors = properties.Where(property => !removed.Any(row => ReferenceEquals(row, property))).ToArray();
                if (node is BirFileDeclaration owner) owner.Properties = survivors;
                else ((IBirTypeDeclaration)node).Properties = survivors;
            }
            node.VisitOwnChildSlots((_, role, slot) => {
                if (role != BirGenericFrameRole.FrozenSourceFact) Walk(slot.Value);
            });
        }
        Walk(file);
    }

    // Exact local-before-reference selection uses the existing authorities. A
    // malformed/incomplete signature is deliberately an unknown signature.
    static (string Name, bool Virtual) AllocateCallName(string name, string kind, TypeNode ownerSlot,
        int count, TypeNode[] signature, int arity, bool super, ReferenceMetadataIndex refs,
        IReadOnlyDictionary<MemberCallSubstitution.LocalPropertyAccessorKey,
            IReadOnlyList<MemberCallSubstitution.LocalPropertyAccessor>> locals)
    {
        var physical = PhysicalName(name, kind);
        if (ownerSlot is not TypeNode.Fqn ownerType) return (physical, false);
        var owner = ReferenceMetadataIndex.BareOwnerFqn(ownerType.Name);
        if (signature != null && (signature.Length != count || signature.Any(type => type == null))) signature = null;
        var ownerArgs = ownerType.Args ?? Array.Empty<TypeNode>();
        if (locals != null && MemberCallSubstitution.TryResolveLocalPropertyAccessor(locals,
            owner, name, kind, arity, count, signature, ownerArgs, out var local)) return (local, false);
        if (refs != null && refs.TryKotlinPropertyAccessor(owner, name, kind, count, arity,
            signature, ownerArgs, out var referenced, out var isVirtual)) return (referenced, isVirtual && !super);
        return (physical, false);
    }
}
