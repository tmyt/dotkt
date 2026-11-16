using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

static partial class KotlinPropertyAccessors
{
    // A generated copy default reads a primary-constructor property, whose
    // default getter observes exactly that storage. Bind the source property
    // through its declaration metadata rather than addressing its CLR backing
    // field by the Kotlin name. Field-only properties retain their field edge.
    internal static IBirExpression BindImportedCopyDefault(IBirExpression expression, ReferenceMetadataIndex refs)
        => (IBirExpression)((BirGraphValue)expression).Rewrite(node =>
        {
            if (node is not BirFieldGet { DataClassCopyDefault: { HasValue: true, Value: true },
                    OwnerType: { HasValue: true, Value: TypeNode.Fqn owner },
                    Name.HasValue: true, Recv.HasValue: true } field
                || !refs.TryKotlinPropertyAccessor(owner.Name, field.Name.Value, "get", 0, 0,
                    Array.Empty<TypeNode>(), owner.Args ?? Array.Empty<TypeNode>(), out var method, out var isVirtual))
                return node;
            return new BirCallInstance {
                OwnerType = field.OwnerType, Method = method, PropertyName = field.Name, PropertyAccessor = "get",
                Recv = field.Recv, Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>(),
                Virtual = isVirtual, Ret = field.Ret, Sty = field.Sty,
                MemberVisibility = field.MemberVisibility, MemberOwnerTypeParams = field.MemberOwnerTypeParams,
                MemberSignature = Array.Empty<TypeNode>(), MemberReturnType = field.MemberType,
            };
        });
}
