// Where a collection token SITS decides which CLR interface it becomes (#370 regression battery).
//
// A read-only Kotlin collection lowers to its covariant CLR face (`IReadOnlyList`) in a head position, but to the
// invariant sibling (`IList`) when it sits in a generic ARGUMENT position — BirTypeLowering's Root-V collapse.
// A member reference has to spell the member the way the TARGET declares it, so a reference authored by walking a
// signature with one uniform alias step names a member that does not exist: `List<List<T>>` came out as
// `IReadOnlyList<IReadOnlyList<T>>` where the runtime stdlib declares `IReadOnlyList<IList<T>>`.
//
// The pairing is what makes this a fixture rather than an example. `collectionInBothPositionsOfOneCall` puts the
// SAME Kotlin type in both positions of a SINGLE call — `List<String>` as the receiver's element (a storage slot,
// which collapses) and as the lambda's parameter (a method slot, which must not) — so a rule that is uniform in
// either direction breaks one half of one call. No single-position test can catch that.
//
// These assert values, but their real assertion is that they COMPILE: a mis-spelled reference fails the exact
// member lookup at emit time, naming the member and the candidates it did not match.
//
// Assembly-wide collision rule: the sole top-level helper is `CollectionTypePosition`-prefixed.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

class CollectionTypePositionTests {
    // ---- generic-ARGUMENT position: the token collapses to the invariant sibling ------------------------------

    // Direct element extraction crosses the physical sibling seam created by Root-V: the member returns the
    // invariant nested face while Kotlin exposes the read-only face. bir2cir must state that view conversion in
    // CIR; leaving it to ilemit both violates layer ownership and produces unverifiable IL when it is omitted.

    @TestAttribute
    fun nestedCollectionInAReturnedElementSlot() {
        // `List<List<T>>` — the inner token is the returned collection's element, a storage slot. This is the
        // shape that was mis-spelled: the reference said `IReadOnlyList<IReadOnlyList<T>>`.
        val w: List<List<Int>> = listOf(1, 2, 3, 4).windowed(2, 1)
        assertEquals(3, w.size)
        val first: List<Int> = w[0]
        assertEquals(2, first.size)
        assertEquals(1, first[0])
        assertEquals(2, first[1])
        val f: List<Int> = w.flatten()
        assertEquals(6, f.size)
        assertEquals(1, f[0])
        assertEquals(4, f[5])
    }

    @TestAttribute
    fun nestedCollectionThroughChunked() {
        val c: List<List<Int>> = listOf(1, 2, 3, 4, 5).chunked(2)
        assertEquals(3, c.size)
        val last: List<Int> = c[2]
        assertEquals(1, last.size)
        assertEquals(5, last[0])
        val f: List<Int> = c.flatten()
        assertEquals(5, f.size)
        assertEquals(5, f[4])
    }

    @TestAttribute
    fun nestedCollectionAsAMapValue() {
        // The same collapse one level down a different container: `Map<K, List<V>>`.
        val g: Map<Int, List<String>> = listOf("a", "bb", "cc").groupBy { it.length }
        assertEquals(1, g[1]!!.size)
        assertEquals(2, g[2]!!.size)
        assertEquals("a", g[1]!![0])
    }

    // ---- both positions at once: the control that no uniform rule can satisfy ---------------------------------

    @TestAttribute
    fun collectionInBothPositionsOfOneCall() {
        // `List<String>` is the receiver's ELEMENT (storage — collapses to the invariant sibling) and the
        // lambda's PARAMETER (a function type's slot — keeps the covariant face). One call, one Kotlin type,
        // two required spellings.
        val joined: List<String> = listOf(listOf("a", "b"), listOf("c")).map { it.joinToString("") }
        assertEquals(2, joined.size)
        assertEquals("ab", joined[0])
        assertEquals("c", joined[1])
    }

    // ---- the other collapses the same lowering applies, which a positional rule alone does not cover ----------

    @TestAttribute
    fun comparableStarCollapsesToTheNonGenericFace() {
        // `Comparable<*>` lowers to the NON-generic `System.IComparable` — contravariance means no value type is
        // `IComparable<object>`. This is the vararg `compareBy`, whose parameter carries that type; a serializer
        // that applied only the arg-position rule spells it `IComparable`1<Object>`, which the runtime stdlib
        // does not declare, and the call fails to resolve at emit time.
        val people = listOf("bb" to 2, "a" to 1, "ccc" to 3)
        val sorted = people.sortedWith(compareBy({ it.second }, { it.first }))
        assertEquals("a", sorted[0].first)
        assertEquals("bb", sorted[1].first)
        assertEquals("ccc", sorted[2].first)
    }

    // ---- head position: the covariant face, the rule everything else is measured against ----------------------

    @TestAttribute
    fun headPositionKeepsTheReadOnlyFace() {
        val flat: List<Int> = listOf(listOf(1, 2), listOf(3)).flatten()
        assertEquals(3, flat.size)
        assertEquals(3, flat[2])
    }
}
