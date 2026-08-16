using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using DotKt.Bir;

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
// sibling to its own implementers, which may name only the interface).
static class ReadOnlyCollectionViewInterfaces
{
    public static void Apply(JsonNode root)
    {
        if (root is not JsonObject o || o["types"] is not JsonArray types) return;
        foreach (var type in types.OfType<JsonObject>())
        {
            if (type["interfaces"] is not JsonArray ifaces) continue;
            var stated = ifaces.Select(TypeJson.Read).OfType<TypeNode.Fqn>().ToList();
            // Declaration order is the emitted InterfaceImpl order: keep the stated faces first, then the siblings
            // they oblige, in the order those faces were stated.
            foreach (var sibling in stated.Select(CollectionViewFaces.ReadOnlySibling).OfType<TypeNode.Fqn>().ToList())
            {
                if (stated.Contains(sibling)) continue;
                stated.Add(sibling);
                ifaces.Add(TypeJson.Write(sibling));
            }
        }
    }
}
