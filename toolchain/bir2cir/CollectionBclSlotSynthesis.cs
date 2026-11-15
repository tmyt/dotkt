using System;
using DotKt.Bir;

// BCL-only collection-interface slots. A Kotlin `MutableCollection<E>` is `@ClrTypeAlias("System.Collections.Generic.
// ICollection")` and `MutableList<E>` is `IList` — but `ICollection<T>`/`IList<T>` carry members Kotlin's collection
// interfaces do NOT: `Contains`, `CopyTo`, `IsReadOnly` (ICollection) and `IndexOf` (IList). Kotlin has the
// value-returning `contains`/`indexOf` under LOWERCASE names (not @ClrIntrinsic-renamed → DeclarationRename leaves them
// lowercase), and has NO equivalent for `CopyTo`/`IsReadOnly` at all. So a Kotlin class implementing the aliased
// interface (kotlin.collections.AbstractMutable{Collection,List,Set}, a MutableMap keys/values view, a user class) is
// missing those BCL slots → the CLR loader rejects any CONCRETE type in the hierarchy ("Method 'Contains' ... does not
// have an implementation" → TypeLoadException). Concrete Kotlin-backed collections need these native slots regardless
// of whether other collection factories happen to allocate native BCL storage.
//
// Fill each missing slot with an ordinary public forwarding member, following the constructed interface closure.
// A class base already supplies its native faces; inherit those implementations rather than replacing their policy.
// An IList face also reaches ICollection through that same graph. Contains and
// IndexOf dispatch through the already-resolved Kotlin query slots, which own the ordinary/storage argument
// boundary and the selected implementation (including renamed overrides). CopyTo consumes the exact CLR IEnumerable<E> face.
// IsReadOnly returns false. The return-DROPPING slots
// (Add/set_Item/RemoveAt) join the common late KotlinOverrideSlotBridge allocation. Non-ref builds only (the ref surface
// stays pure Kotlin). Modeled on ComparableBridgeSynthesis.
static partial class CollectionBclSlotSynthesis
{
    const string ICollection = "System.Collections.Generic.ICollection";
    const string IList = "System.Collections.Generic.IList";

    static string FaceKey(TypeNode.Fqn face) => SupertypeGraph.TypeKey(
        BirTypeLowering.CanonicalPhysicalSlotType(CollectionFace(face)));

    internal static void SelfTest()
    {
        TypeNode.Fqn Face(string owner, TypeNode element) => new(owner, new[] { element });
        var keyword = Face(ICollection, new TypeNode.Array(new TypeNode.Fqn("int")));
        var qualified = Face(ICollection + "`1", new TypeNode.Array(new TypeNode.Fqn("System.Int32")));
        if (FaceKey(keyword) != FaceKey(qualified)
            || FaceKey(Face(ICollection, new TypeNode.Tv("type", 0)))
                == FaceKey(Face(ICollection, new TypeNode.Tv("type", 1)))
            || FaceKey(qualified) == FaceKey(Face(IList, qualified.Args[0])))
            throw new InvalidOperationException("Native collection faces lost physical identity or exact frames");
        Console.WriteLine("[collection native faces] self-test OK (physical element identity, distinct frames and faces)");
    }
    // A referenced graph uses exact CLR TypeDef punctuation; locally lowered aliases use the open name.
    // Compare that same declaration identity with its authoritative argument vector, never a source-name prefix.
    static TypeNode.Fqn CollectionFace(TypeNode.Fqn face)
    {
        if (face.Args is not { Length: 1 }) return null;
        var name = ReferenceMetadataIndex.BareOwnerFqn(face.Name);
        return name is ICollection or IList ? new TypeNode.Fqn(name, face.Args) : null;
    }
}
