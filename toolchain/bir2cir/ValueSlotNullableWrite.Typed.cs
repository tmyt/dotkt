using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class ValueSlotNullableWrite
{
    internal static void Apply(BirFileDeclaration root, ReferenceMetadataIndex refs, ValueTypeOracle isValue)
        => SourceGraphRewrite.Rewrite(root, (node, scope) => {
            if (node is BirPropertySet { Value.HasValue: true } write)
            {
                var owner = UnwrapOwnerFqn(write.Type.OrDefault());
                if (owner == null || !write.Name.HasValue) return node;
                var reflected = refs.ResolveNetType(ReferenceMetadataIndex.ReflectedOwnerFqn(owner.Name), owner.Args?.Length ?? 0);
                if (reflected == null) return node;
                var source = BirGraphStaticType.Surface(write.Value.Value, scope);
                var slot = ClrMemberResolution.PropertySlotType(reflected, write.Name.Value, write: true,
                    isStatic: write.Static.OrDefault());
                if (IsNullSource(write.Value.Value, source) && IsBareValueSlot(slot, owner.Args, isValue))
                    throw new InvalidOperationException(
                        $"bir2cir: cannot assign `null` to the value-type platform slot `{owner.Name}.{write.Name.Value}` — a CLR "
                        + "value type has no null representation. Use an explicit Kotlin `Int?`-typed property for nullable value storage.");
                var target = ConcreteValueSlot(slot, owner.Args, source, isValue);
                if (target != null && NullableTvErasureCallRealign.CoerceForFixedSlot(write.Value.Value, source, target, isValue) is { } coerced)
                    write.Value = OptionalField<IBirExpression>.Of(coerced);
            }
            else if (node is BirPropertyGet read)
            {
                var owner = UnwrapOwnerFqn(read.Type.OrDefault());
                if (owner == null || !read.Name.HasValue) return node;
                var reflected = refs.ResolveNetType(ReferenceMetadataIndex.ReflectedOwnerFqn(owner.Name), owner.Args?.Length ?? 0);
                if (reflected == null) return node;
                var surface = BirGraphStaticType.Surface((IBirExpression)read, scope);
                var slot = ClrMemberResolution.PropertySlotType(reflected, read.Name.Value, write: false,
                    isStatic: read.Static.OrDefault());
                var target = ConcreteValueSlot(slot, owner.Args, surface, isValue);
                if (target == null) return node;
                read.Ret = target;
                if (read.Sty.HasValue) read.Sty = target;
            }
            return node;
        });

    static bool IsNullSource(IBirExpression value, TypeNode source)
        => source is TypeNode.Nullable { Of: TypeNode.Fqn { Name: "kotlin.Nothing" } }
            || value is BirConstant { Value.IsNull: true };
}
