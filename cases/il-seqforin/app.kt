// A `for (x in seq)` over a Kotlin `Sequence` must lower through the SAME GetEnumerator (forEachInline) path as an
// Iterable — Sequence is @ClrTypeAlias(IEnumerable). Otherwise a synthesized monomorphized iterator interface the rt
// SequenceBuilderIterator doesn't implement -> runtime EntryPointNotFound.
fun main() {
    for (x in sequence { yield("a"); yield("b") }) println(x)
}
