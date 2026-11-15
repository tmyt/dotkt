using System;
using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// Late CLR bridge synthesis must consume the allocated iterator ABI, not recreate its Kotlin generic signature.
static class KotlinIteratorPhysicalProtocol
{
    const string Iterator = "kotlin.collections.Iterator";

    internal static TypeNode Carrier(ReferenceMetadataIndex refs) =>
        refs.TryExistentialPhysicalOwner(Iterator, out var carrier)
            ? new TypeNode.Fqn(carrier)
            : throw new InvalidOperationException("Iterator declaration has no existential physical owner");

    internal static IBirExpression CallSource(ReferenceMetadataIndex refs, IBirExpression receiver,
        string member, TypeNode element, TypeNode result)
    {
        var (owner, name, signature, physicalReturn) = Plan(refs, member, element);
        var call = new BirCallInstance {
            OwnerType = new TypeNode.Fqn(owner), Virtual = true,
            Recv = OptionalField<IBirExpression>.Of(receiver), Method = name,
            Sig = signature.Select(BirGraphValue.CopyType).ToArray(), Ret = BirGraphValue.CopyType(physicalReturn),
            Args = Array.Empty<IBirExpression>(),
        };
        // The caller supplies a fresh read or transfers its actual occurrence.
        // The final physical member binder, not this protocol selection, owns MemberRef.
        return physicalReturn.Equals(result) ? call : new BirCast {
            Type = BirGraphValue.CopyType(result), E = OptionalField<IBirExpression>.Of(call),
        };
    }

    static (string Owner, string Name, TypeNode[] Signature, TypeNode Return) Plan(
        ReferenceMetadataIndex refs, string member, TypeNode element)
    {
        if (!refs.TryStarProjectionMember(new TypeNode.Fqn(Iterator, new[] { element }), member,
                null, 0, Array.Empty<TypeNode>(), 0, null, out var owner, out var name,
                out var signature, out _, out var physicalReturn))
            throw new InvalidOperationException($"Iterator member {member} has no existential physical slot");
        return (owner, name, signature, physicalReturn);
    }
}
