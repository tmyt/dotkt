// SHARED collection-face relation of the CIR contract. Linked into bir2cir (which STATES the faces) and ilemit
// (whose IrSanity gate REFUSES a document that omits one), so producer and boundary check cannot drift apart.
//
// Kotlin's collection hierarchy relates the mutable and read-only interfaces — `MutableList<E>` IS-A `List<E>` —
// but the CLR faces they lower to do not: `IList<T>` does not derive from `IReadOnlyList<T>`, nor `ICollection<T>`
// from `IReadOnlyCollection<T>`. So a Kotlin value flowing from a mutable type into a read-only slot, which the
// source language performs with no conversion at all, is a castclass into an unrelated interface on the CLR. It is
// total only when the object's own type declares the read-only face. The BCL's mutable collections (List<T>,
// HashSet<T>) do; an emitted Kotlin type that named only the mutable face would not.
//
// Hence: every emitted type naming a mutable collection face also declares its read-only sibling. `IReadOnlyList<T>`
// already derives from `IReadOnlyCollection<T>`, so the list face needs only the former.

namespace DotKt.Bir;

public static class CollectionViewFaces
{
    public const string IList = "System.Collections.Generic.IList";
    public const string ICollection = "System.Collections.Generic.ICollection";
    public const string ISet = "System.Collections.Generic.ISet";
    public const string IReadOnlyList = "System.Collections.Generic.IReadOnlyList";
    public const string IReadOnlyCollection = "System.Collections.Generic.IReadOnlyCollection";

    /// <summary>
    /// The read-only face a stated mutable collection face obliges, or null when the face is not a mutable
    /// collection one. Keyed on the lowered BCL interface identity — never on a Kotlin type or member name.
    /// </summary>
    public static TypeNode.Fqn ReadOnlySibling(TypeNode.Fqn face) =>
        face?.Args is not { Length: 1 } args ? null
        : face.Name switch
        {
            IList => new TypeNode.Fqn(IReadOnlyList, new[] { args[0] }),
            ICollection or ISet => new TypeNode.Fqn(IReadOnlyCollection, new[] { args[0] }),
            _ => null,
        };
}
