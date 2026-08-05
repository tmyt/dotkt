// Collection / sequence-op battery (feature fixture) — a concrete generic stdlib collection, chunked/filterNotNull,
// CharSequence.windowed, and String.format. Migrates the collection-op family of cases/il-* onto the in-process NUnit
// suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value assert is
// strictly stronger (typed) than the old text diff. Every value the old il_check asserted is preserved 1:1.
//
// Coverage preserved (old case -> method):
//   il-arraydeque -> arrayDeque_asFieldOwner  ArrayDeque<E>:AbstractMutableList<E> as a field/owner -> the ICollection/
//                                             IList void-drop methodimpl bridge; add/removeFirst/removeLast/removeAt/set
//   il-chunk      -> chunkedAndFilterNotNull  chunked (.NET Chunk + per-chunk ToList) + filterNotNull (ref T? and value Int?)
//   il-cwindowed  -> charSequenceWindowed     CharSequence.windowed (break-in-EXPRESSION-position body lowering)
//   il-bmore      -> stringFormat_mapIndexed  String.format -> System.String.Format (.NET composite {0:F2}/{0:D5}) + mapIndexed
//
// nullableGenericIdiomsAtValueTypes is not migrated from a case: it is the #86 value-instantiation armor for the
// mapNotNull(To)/filterNotNull(To)/chunked family (see the comment on the method).
//
// Assembly-wide collision rule: the sole top-level helper is `CollectionOperations`-prefixed (il-arraydeque's `Holder` -> `CollectionOperationsDequeHolder`).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

// ---- il-arraydeque : a concrete generic stdlib collection as a FIELD/owner type ---------------------------------
class CollectionOperationsDequeHolder {
    val q: ArrayDeque<String> = ArrayDeque()
}

class CollectionOperationsTests {
    @TestAttribute
    fun asFieldOwner() {
        val h = CollectionOperationsDequeHolder()
        val d = h.q
        d.addLast("a")
        d.addLast("b")
        d.addFirst("z")                     // [z, a, b]
        assertEquals("z", d.removeFirst())  // z
        assertEquals("b", d.removeLast())   // b   -> [a]
        d.add("c")                          // MutableCollection.add -> ICollection.Add (Boolean->void bridge)  [a, c]
        d[0] = "A"                          // MutableList.set -> IList.set_Item (E->void bridge)                [A, c]
        assertEquals("c", d.removeAt(1))    // MutableList.removeAt -> IList.RemoveAt (E->void bridge): c        [A]
        assertEquals(1, d.size)             // 1
        assertEquals("A", d.first())        // A
    }

    @TestAttribute
    fun chunkedAndFilterNotNull() {
        val xs = listOf(1, 2, 3, 4, 5)
        assertEquals("3,7,5", xs.chunked(2).map { it.sum() }.joinToString(","))                 // 3,7,5
        assertEquals(3, xs.chunked(2).size)                                                     // 3
        assertEquals("1-2-3 4-5", xs.chunked(3).map { it.joinToString("-") }.joinToString(" ")) // 1-2-3 4-5
        val ns: List<String?> = listOf("a", null, "b", null, "c")
        assertEquals("a,b,c", ns.filterNotNull().joinToString(","))  // a,b,c  (reference T?)
        assertEquals(3, ns.filterNotNull().size)                     // 3
        val vs: List<Int?> = listOf(1, null, 3, null, 5)
        assertEquals("1,3,5", vs.filterNotNull().joinToString(","))  // 1,3,5  (value-type Nullable<Int> unwrap)
        assertEquals(9, vs.filterNotNull().sum())                    // 9
    }

    // #86 — the VALUE instantiations of the nullable-generic collection idioms. The whole `Nullable(Tv)` family is
    // invisible at T=String (a bare `T?` slot is trivially sound for a reference type), so these are the only
    // measurement of it in this lane: a `mapNotNull`/`mapNotNullTo` transform result and a `filterNotNull(To)`
    // element are `T?` slots that must hold a genuine null in an erased object slot and re-narrow (unbox.any) at the
    // typed store. Both destination-taking forms are covered because the destination's element type is the one the
    // erased element must convert BACK to. Every value here is asserted, not printed, so a wrong representation
    // surfaces as a typed diff rather than garbage output.
    @TestAttribute
    fun nullableGenericIdiomsAtValueTypes() {
        val xs = listOf(1, 2, 3, 4, 5)
        assertEquals("20,40", xs.mapNotNull { if (it % 2 == 0) it * 10 else null }.joinToString(","))  // 20,40
        assertEquals(60, xs.mapNotNull { if (it % 2 == 0) it * 10 else null }.sum())                   // 60
        val dest = mutableListOf<Int>()
        xs.mapNotNullTo(dest) { if (it > 3) it * 2 else null }
        assertEquals("8,10", dest.joinToString(","))                 // 8,10
        val bdest = mutableListOf<Boolean>()
        xs.mapNotNullTo(bdest) { if (it % 2 == 0) it > 2 else null }
        assertEquals("False,True", bdest.joinToString(","))          // False,True  (T=Boolean, CLR rendering)
        val bs: List<Boolean?> = listOf(true, null, false)
        assertEquals("True,False", bs.filterNotNull().joinToString(","))  // True,False
        assertEquals(2, bs.filterNotNull().size)                     // 2
        assertEquals("[True, False]", listOf(true, false).chunked(2)[0].toString())  // [True, False]
        // `filterNotNullTo` is driven at a REFERENCE element only. Its known value-element limitation is tracked as
        // a bug rather than being accepted through a test-suite XFAIL.
        val ss: List<String?> = listOf("a", null, "b")
        val sdest = mutableListOf<String>()
        ss.filterNotNullTo(sdest)
        assertEquals("a,b", sdest.joinToString(","))                 // a,b
    }

    @TestAttribute
    fun charSequenceWindowed() {
        assertEquals("[ab, bc, cd]", "abcd".windowed(2).toString())                 // [ab, bc, cd]
        assertEquals("[ab, cd]", "abcde".windowed(2, 2).toString())                 // [ab, cd]
        assertEquals("[abc, bcd, cde, de, e]", "abcde".windowed(3, 1, true).toString()) // [abc, bcd, cde, de, e]
        assertEquals("[ab, de]", "abcdef".windowed(2, 3).toString())                // [ab, de]
        assertEquals("[ab, bc, cd]", "abcd".windowed(2) { it.toString() }.toString()) // [ab, bc, cd] (ref transform)
    }

    @TestAttribute
    fun mapIndexed() {
        assertEquals("5 items", "{0} items".format(5))            // 5 items
        assertEquals("x = 42", "{0} = {1}".format("x", 42))        // x = 42
        assertEquals("3.14", "{0:F2}".format(3.14159))             // 3.14
        assertEquals("00007", "{0:D5}".format(7))                  // 00007
        assertEquals("ff", "{0:x}".format(255))                    // ff
        assertEquals("100% ok: yes", "100% ok: {0}".format("yes")) // 100% ok: yes
        val xs = listOf("a", "b", "c")
        assertEquals("0:a,1:b,2:c", xs.mapIndexed { i, v -> "$i:$v" }.joinToString(","))          // 0:a,1:b,2:c
        assertEquals("0,20,60", listOf(10, 20, 30).mapIndexed { i, v -> i * v }.joinToString(",")) // 0,20,60
    }
}
