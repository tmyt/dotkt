using DotKt.Bir;
using DotKt.Bir.Lowering;

// Count on a physical raw collection cast binds to System.Collections.ICollection.Count, not a generic
// IReadOnlyCollection<object> slot. StarProjectionLowering still produces raw IDictionary for Map; Collection
// and List families instead use their composite classifier/member paths. Explicit raw BCL casts can also occur.
// App build only.
static partial class StarProjectionCountLowering
{
    internal static void Apply(BirFileDeclaration root)
    {
        void Walk(BirGraphValue node)
        {
            if (node is IBirExecutablePayload) return;
            if (node is BirPropertyGet { Name.HasValue: true, Recv.HasValue: true } property
                && property.Name.Value == "Count"
                && property.Recv.Value is BirCast { Type.HasValue: true } cast
                && IsNonGenericBclCollection(cast.Type.Value))
            {
                property.Type = new TypeNode.Fqn("System.Collections.ICollection");
                cast.Type = new TypeNode.Fqn("System.Collections.ICollection");
            }
            node.VisitOwnChildSlots((_, role, slot) => {
                if (!NullableRepresentationViews.Frozen(role)) Walk(slot.Value);
            });
        }
        Walk(root);
    }

    // Raw BCL interfaces that inherit ICollection.Count. IEnumerable has no Count.
    static bool IsNonGenericBclCollection(TypeNode t) => t is TypeNode.Fqn { Args: null } f
        && f.Name is "System.Collections.ICollection" or "System.Collections.IList" or "System.Collections.IDictionary";
}
