// Collection values retain their Kotlin meaning across signature and storage positions (#370 regression battery).
// Ordinary read-only values use an opaque identity-preserving ABI; native declarations and implemented interfaces
// keep their exact CLR faces. Nested values, returned elements and callback slots must agree with that same contract,
// rather than manufacturing a different reified collection construction at each use.
//
// These assert values, but their real assertion is that they COMPILE: a mis-spelled reference fails the exact
// member lookup at emit time, naming the member and the candidates it did not match.
//
// Assembly-wide collision rule: the sole top-level helper is `CollectionTypePosition`-prefixed.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.clr.ClrField

class CollectionTypePositionCell<T>(initial: T) {
    @ClrField var item: T = initial
}

class CollectionTypePositionTests {
    // ---- generic argument positions -------------------------------------------------------------------------

    // Direct element extraction must preserve the nested collection's ordinary value representation. bir2cir owns
    // that representation and all conversion edges; ilemit emits the already-resolved signature one-to-one.

    @TestAttribute
    fun nestedCollectionInAReturnedElementSlot() {
        // The nested list is a returned element as well as a declared local value.
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
        // Read-only values remain read-only inside a different container too: `Map<K, List<V>>`.
        val g: Map<Int, List<String>> = listOf("a", "bb", "cc").groupBy { it.length }
        assertEquals(1, g[1]!!.size)
        assertEquals(2, g[2]!!.size)
        assertEquals("a", g[1]!![0])
    }

    // ---- storage and callback positions in the same call -----------------------------------------------------

    @TestAttribute
    fun collectionInBothPositionsOfOneCall() {
        // `List<String>` is both the receiver's element and the lambda's parameter.
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

    // ---- direct read-only values ----------------------------------------------------------------------------

    @TestAttribute
    fun headPositionKeepsTheReadOnlyFace() {
        val flat: List<Int> = listOf(listOf(1, 2), listOf(3)).flatten()
        assertEquals(3, flat.size)
        assertEquals(3, flat[2])
    }

    @TestAttribute
    fun conditionalBranchesAndGenericFieldsCarryTheirPhysicalViews() {
        val mutable: MutableList<Int> = mutableListOf(1, 2)
        val readonly: List<Int> = listOf(3, 4)
        val fromMutable: List<Int> = if (mutable.size == 2) mutable else readonly
        assertEquals(1, fromMutable[0])

        val mutableCell: CollectionTypePositionCell<List<Int>> = CollectionTypePositionCell(readonly)
        assertEquals(3, mutableCell.item[0])
        mutableCell.item = mutable
        assertEquals(2, mutableCell.item[1])
    }
}
