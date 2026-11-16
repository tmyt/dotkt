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
                || !refs.TryKotlinPropertyAccessorBinding(owner.Name, field.Name.Value, "get", 0, 0,
                    Array.Empty<TypeNode>(), owner.Args ?? Array.Empty<TypeNode>(), out var getter))
                return node;
            return new BirCallInstance {
                OwnerType = field.OwnerType, Method = getter.Name, PropertyName = field.Name, PropertyAccessor = "get",
                DeclarationId = getter.DeclarationId != null ? OptionalField<string>.Of(getter.DeclarationId) : default,
                Recv = field.Recv, Args = Array.Empty<IBirExpression>(), Sig = Array.Empty<TypeNode>(),
                Virtual = getter.IsVirtual, Ret = field.Ret, Sty = field.Sty,
                MemberVisibility = getter.PhysicalVisibility, MemberOwnerTypeParams = field.MemberOwnerTypeParams,
                MemberSignature = getter.SignatureParameters.Select(BirGraphValue.CopyType).ToArray(),
                MemberReturnType = BirGraphValue.CopyType(getter.SignatureReturn),
            };
        });
}
