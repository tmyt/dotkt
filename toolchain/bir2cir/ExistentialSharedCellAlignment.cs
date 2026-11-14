using DotKt.Bir;

// A mutable capture's heap cell keeps the original Kotlin generic frame, but its value field must accept the
// representation actually written to it. Only cells explicitly assembled from refTypes participate: neither
// ordinary CLR fields nor generated-class names/layout are evidence of mutable-capture storage.
static partial class ExistentialSharedCellAlignment
{
    static TypeNode Core(TypeNode type) => type switch
    {
        TypeNode.Nullable nullable => Core(nullable.Of),
        TypeNode.Oblivious oblivious => Core(oblivious.Of),
        _ => type,
    };
}
