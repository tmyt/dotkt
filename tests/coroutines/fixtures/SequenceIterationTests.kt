// Sequence-builder iteration battery — migrates the `sequence{}` cold-core cases onto the in-process NUnit
// suite. The sequence-builder shares the coroutine cold-core lowering (yield -> a suspend suspension point), so
// it belongs in the coroutine lane. No blockOn harness needed: `for (x in seq)` drives the enumerator directly.
// Each old case's `main` + stdout-golden becomes one @TestAttribute method preserving every asserted value 1:1.
//
// Coverage preserved (old case -> method):
//   il-seqforin -> seqforin_forInOverSequence
//                  a `for (x in seq)` over a Kotlin `Sequence` must lower through the SAME GetEnumerator
//                  (forEachInline) path as an Iterable — Sequence is @ClrTypeAlias(IEnumerable). Otherwise a
//                  synthesized monomorphized iterator interface the rt SequenceBuilderIterator doesn't implement
//                  -> runtime EntryPointNotFound. The former println per element is collected into a list and
//                  asserted positionally.
//
// No top-level declarations, so there is nothing to prefix.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

class SequenceIterationTests {
    @TestAttribute
    fun forInOverSequence() {
        val out = mutableListOf<String>()
        for (x in sequence { yield("a"); yield("b") }) out.add(x)
        assertEquals(2, out.size)
        assertEquals("a", out[0])   // former golden line 1
        assertEquals("b", out[1])   // former golden line 2
    }
}
