using System.Linq;
using DotKt.Bir;
using DotKt.Bir.Lowering;
using DotKt.Cir;

// The READ-ONLY face of a mutable collection implementer, stated in CIR.
//
// Why the face is owed at all is the shared relation in bir-common/CollectionViewFaces.cs: Kotlin's
// `MutableList<E>` IS-A `List<E>`, but the CLR faces they lower to (`IList<T>` / `IReadOnlyList<T>`) are unrelated
// interfaces, so the read-only view of an emitted type is real only when that type declares it. Deciding which CLR
// interfaces a Kotlin declaration becomes is bir2cir's; ilemit emits the `interfaces` array one-to-one and infers
// no sibling, and its IrSanity gate refuses a document that omits one.
//
// This pass authors no MethodImpl. The sibling's members (`get_Count`, `get_Item`, `GetEnumerator`) are the same
// names and signatures the mutable face already forced the type to declare as public virtual methods, and the CLR
// binds an interface slot to a matching public virtual method implicitly, per interface. Where a member's physical
// name does NOT match — a Kotlin `size` renamed to `prop_get<size>` — the MethodImpl already exists: the Kotlin
// supertype graph names `Collection`/`List` as supertypes of `MutableCollection`/`MutableList`, so
// KotlinOverrideSlotBridge stated those slots long before this pass runs.
//
// ilemit's own interface-slot wiring, which #400 will delete, does emit a redundant explicit MethodImpl once the
// sibling is a stated face: it binds the read-only `get_Item` slot to the same public method the implicit binding
// would have selected. That is the emitter's inference, not this pass's decision, and it restates rather than
// changes the binding.
//
// The rule is structural: keyed on the BCL interface identity a declaration lowered to, never on a Kotlin type or
// member name, and applied to classes and interfaces alike (an interface extending the mutable face must expose the
// sibling to its own implementers, which may name only the interface). CollectionBclSlotSynthesis completes the
// BCL-only slots from that same constructed interface closure before this pass runs.
static partial class ReadOnlyCollectionViewInterfaces
{
    internal static void Apply(BirFileDeclaration root)
    {
        foreach (var type in root.Types.OrEmpty())
        {
            if (!type.Interfaces.HasValue) continue;
            var interfaces = type.Interfaces.Value.ToList();
            var stated = interfaces.OfType<TypeNode.Fqn>().ToList();
            // Keep actual original edges first, followed by the first occurrence
            // of each owed sibling in original declaration order, including interfaces.
            foreach (var sibling in stated.Select(CollectionViewFaces.ReadOnlySibling).OfType<TypeNode.Fqn>().ToArray())
            {
                if (stated.Contains(sibling)) continue;
                stated.Add(sibling);
                interfaces.Add(sibling);
            }
            type.Interfaces = interfaces.ToArray();
        }
    }
}
