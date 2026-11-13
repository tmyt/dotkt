using DotKt.Bir;

// Array representation facts shared by source construction and later physical
// consumers. Primitive names and reference-array element identity are resolved
// here, not reconstructed by the emitter.
static partial class ArrayConstructionLowering
{
    internal static TypeNode ArrayElementOf(TypeNode type) => type switch {
        TypeNode.Array array => array.Elem,
        TypeNode.Nullable nullable => ArrayElementOf(nullable.Of),
        TypeNode.Oblivious oblivious => ArrayElementOf(oblivious.Of),
        TypeNode.Fqn owner when BirTypeLowering.PrimArrayElem.TryGetValue(owner.Name, out var element)
            => new TypeNode.Fqn(element),
        TypeNode.Fqn { Name: "kotlin.Array", Args: { Length: 1 } arguments } => arguments[0],
        _ => null,
    };

    // An unsigned specialized array's storage-taking constructor is not the
    // Int-size constructor. Keep its declared array slot for TransformNew.
    static bool IsArrayTypeNode(TypeNode type) => type switch {
        TypeNode.Array => true,
        TypeNode.Fqn owner when owner.Args == null && BirTypeLowering.PrimArrayElem.ContainsKey(owner.Name) => true,
        TypeNode.Fqn { Name: "kotlin.Array" } => true,
        _ => false,
    };
}
