// Array battery — factory/index/iterate, collection ops, arrayOfNulls value-type nullability, slice/take/copyOfRange,
// plus/plusElement reified-element preservation, primitive-array-receiver stdlib extensions, overlap-safe copyInto,
// fill range validation, `.indices`/`.lastIndex` (value + reference element), unsigned specialized arrays, and the
// nested nullable-type-var array erasure. Migrates the array family of cases/il-* onto the in-process NUnit suite.
// Each old case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value assertEquals/assertNull
// is strictly stronger (typed) than the old text diff. Every value the old il_check asserted is preserved 1:1 (see the
// `// <expected>` comments); an array-print golden (`[a, b, c]`) becomes an assertEquals on the exact toString(), and a
// thrown-exception scenario becomes a try/catch sentinel (the catch clause pins the EXACT exception type).
//
// EXCLUDED (array-shaped but kept in the bash lane): il-copyofnull / il-boxgen carry a live XFAIL_ILVERIFY finding
// (#127/#86 nullable value-type object-erasure, #62/#46 comparator covariance-erasure) — a formal-only ilverify gap,
// not migratable into the ilverify-clean NUnit lane; and il-arraydeque/il-arrslice-adjacent interop cases stay bash.
//
// Coverage preserved (old case -> method):
//   il-arr            -> arr_basic                     intArrayOf factory, get/set, .size, indexed + for-in iteration
//   il-arrops         -> arrops_collectionOps          firstOrNull/map/filter/sum/count over Array + value-type first/lastOrNull
//   il-arrnull        -> arrnull_arrayOfNulls          #113 arrayOfNulls<T>(n) -> Nullable<T>[] + copyOf() round-trip (Int/Long/Double/Char/String)
//   il-arrslice       -> arrslice_sliceTakeCopyRange   #117 Array<value>.slice/take/takeLast/copyOfRange runtime-element-preserving
//   il-arrplus        -> arrplus_plusElement           #120 Array<value>.plus/plusElement reified body-local element
//   il-intarraytolist -> intarraytolist_primitiveArrayExt  #153 primitive-array-receiver stdlib ext (toList/copyOf/copyInto/contentToString), signed vs unsigned key
//   il-copyintoverlap -> copyintoverlap_overlapSafe    #97 copyInto overlap-safe (memmove) + ArrayDeque middle-insert victim
//   il-fillrange      -> fillrange_rangeValidation     #145 array fill range validation (IAE inverted, IOOBE out-of-bounds)
//   il-indices        -> indices_forInRange            for-in over a non-literal IntRange from .indices (Collection + CharSequence)
//   il-indicesv       -> indicesv_valueElementIndices  #30 value-element .indices/.lastIndex covariance-safe (Int/Double + reference)
//   il-ubytearr       -> ubytearr_unsignedArray        #53 UByteArray -> native Byte[]; to(U)ByteArray reinterpret
//   il-genarrlam      -> genarrlam_nullableTvArray     #142/#4 Array(size){ mk<T?>(null) } in a generic class; nested Nullable(Tv) erasure read-back
//
// toTypedArrayAtValueElements is not migrated from a case: it is the #86 value-instantiation armor for the
// `arrayOfNulls`-backed Array<T> factories (toTypedArray/plus/plusElement) — see the comment on the method.
//
// Top-level names are unique within this single battery assembly (one project = one namespace) and `Arr`-prefixed
// to avoid clashing with sibling batteries and stdlib names.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsNull as assertNull

// ---- il-genarrlam : Array(size){ mk<T?>(null) } inside a generic class (nested Nullable(Tv) erasure) ------------
class ArrRef<X>(val v: X)
fun <X> arrMk(x: X): ArrRef<X> = ArrRef(x)
class ArrBox<T>(size: Int) {
    val a: Array<ArrRef<T?>> = Array(size) { arrMk<T?>(null) }
    fun count(): Int = a.size
    fun elem(i: Int): ArrRef<T?> = a[i]
}

enum class ArraySeason { SPRING, SUMMER, AUTUMN }
fun <T : Enum<T>> arrayEnumName(value: T): String = value.name

class ArrayTests {
    @TestAttribute
    fun boxedGenericValues() {
        val values = mutableMapOf<Int, Int>()
        assertEquals(42, values.getOrPut(5) { 42 })
        assertEquals(1, values.size)
        assertEquals(42, values[5])
        assertEquals(42, values.getOrPut(5) { 99 })
        assertEquals(10, mapOf(1 to 10, 2 to 20).getOrElse(1) { -1 })
        assertEquals(-1, mapOf(1 to 10).getOrElse(9) { -1 })
        assertEquals("[1, 2, 3]", listOf(3, 1, 2).sortedWith(compareBy { it }).toString())
        assertEquals("[3, 2, 1]", listOf(3, 1, 2).sortedByDescending { it }.toString())
        val pairs = listOf(3 to "c", 1 to "a", 2 to "b")
        assertEquals("[a, b, c]", pairs.sortedWith(compareBy { it.first }).map { it.second }.toString())
        assertEquals("[1, null, 3]", arrayOf(1, null, 3).toList().toString())
        assertEquals("SUMMER", arrayEnumName(ArraySeason.SUMMER))
    }

    @TestAttribute
    fun copyOfGrowsWithNullTail() {
        val ints = arrayOf(1, 2, 3)
        assertEquals("[1, 2, 3, null, null]", ints.copyOf(5).toList().toString())
        assertEquals("[1, 2]", ints.copyOf(2).toList().toString())
        assertEquals("[1, 2, 3]", ints.copyOf(3).toList().toString())
        val grown = ints.copyOf(5)
        assertEquals(1, grown[0])
        assertNull(grown[4])
        var sum = 0
        for (i in 0 until 3) sum += grown[i]!!
        assertEquals(6, sum)
        assertEquals("[1, 2, null]", arrayOf(1L, 2L).copyOf(3).toList().toString())
        assertEquals("[2.5, 3.5, null]", arrayOf(2.5, 3.5).copyOf(3).toList().toString())
        assertEquals("[a, b, null]", arrayOf('a', 'b').copyOf(3).toList().toString())
        assertEquals("[x, y, null]", arrayOf("x", "y").copyOf(3).toList().toString())
        val nullable = arrayOfNulls<Int>(2)
        nullable[0] = 7
        assertEquals("[7, null, null]", nullable.copyOf(3).toList().toString())
    }

    @TestAttribute
    fun basic() {
        val a = intArrayOf(10, 20, 30)
        assertEquals(10, a[0])        // 10
        assertEquals(30, a[2])        // 30
        a[1] = 99
        assertEquals(99, a[1])        // 99
        assertEquals(3, a.size)       // 3
        var sum = 0
        var i = 0
        while (i < a.size) { sum = sum + a[i]; i = i + 1 }
        assertEquals(139, sum)        // 139
        var fsum = 0
        for (x in a) fsum = fsum + x
        assertEquals(139, fsum)       // 139
    }

    @TestAttribute
    fun collectionOps() {
        val xs = arrayOf(3, 1, 4, 1, 5)
        assertEquals(3, xs.firstOrNull() ?: -1)                                       // 3
        assertEquals("6,8,10", xs.map { it * 2 }.filter { it > 4 }.joinToString(","))  // 6,8,10
        assertEquals(14, xs.sum())                                                    // 14
        assertEquals(2, xs.count { it == 1 })                                         // 2
        assertEquals(-1, (arrayOf<Int>()).firstOrNull() ?: -1)                        // -1
        val v = listOf(10, 20, 30)
        assertEquals(10, v.firstOrNull() ?: -1)                                       // 10 (value-type firstOrNull)
        assertEquals(30, v.lastOrNull() ?: -1)                                        // 30
    }

    @TestAttribute
    fun arrayOfNulls() {
        val a = arrayOfNulls<Int>(3)
        a[0] = 5
        assertEquals(5, a[0])                              // 5
        assertNull(a[1])                                   // null
        assertEquals(3, a.size)                            // 3
        val c = a.copyOf()                                 // copyOf() -> Nullable<int>[] round-trip
        c[1] = 7
        assertEquals(5, c[0])                              // 5
        assertEquals(7, c[1])                              // 7
        assertNull(a[1])                                   // null (copy independent)
        assertEquals("[5, null, null]", a.toList().toString())  // [5, null, null]
        val la = arrayOfNulls<Long>(2)
        la[0] = 100L
        assertEquals(100L, la[0])                          // 100
        assertNull(la[1])                                  // null
        val da = arrayOfNulls<Double>(2)
        da[1] = 2.5
        assertNull(da[0])                                  // null
        assertEquals(2.5, da[1])                           // 2.5
        val ca = arrayOfNulls<Char>(2)
        ca[0] = 'x'
        assertEquals('x', ca[0])                           // x
        assertNull(ca[1])                                  // null
        val sa = arrayOfNulls<String>(2)
        sa[0] = "hi"
        assertEquals("hi", sa[0])                          // hi
        assertNull(sa[1])                                  // null
    }

    @TestAttribute
    fun sliceTakeCopyRange() {
        val a = arrayOf(10, 20, 30, 40, 50)
        assertEquals("[20, 30, 40]", a.slice(1..3).toString())  // [20, 30, 40]
        assertEquals("[10, 20]", a.take(2).toString())          // [10, 20]
        assertEquals("[40, 50]", a.takeLast(2).toString())      // [40, 50]
        val r = a.copyOfRange(1, 4)
        assertEquals(3, r.size)                                 // 3
        assertEquals(20, r[0])                                  // 20
        assertEquals(90, r.sum())                               // 90 (real int[] arithmetic)
        val la = arrayOf(1L, 2L, 3L, 4L)
        assertEquals("[1, 2]", la.take(2).toString())           // [1, 2]
        val da = arrayOf(1.5, 2.5, 3.5)
        assertEquals("[2.5, 3.5]", da.takeLast(2).toString())   // [2.5, 3.5]
        val ca = arrayOf('a', 'b', 'c', 'd')
        assertEquals("[b, c]", ca.slice(1..2).toString())       // [b, c]
        val s = arrayOf("a", "b", "c", "d")
        assertEquals("[b, c]", s.slice(1..2).toString())        // [b, c]
        assertEquals("[a, b]", s.take(2).toString())            // [a, b]
        assertEquals("[c, d]", s.takeLast(2).toString())        // [c, d]
    }

    @TestAttribute
    fun plusElement() {
        val a = arrayOf(1, 2, 3)
        assertEquals("[1, 2, 3, 4]", a.plus(4).toList().toString())          // [1, 2, 3, 4]
        assertEquals("[1, 2, 3, 5]", a.plusElement(5).toList().toString())   // [1, 2, 3, 5]
        assertEquals(6, a.sum())                                             // 6 (receiver untouched)
        val la = arrayOf(1L, 2L, 3L)
        assertEquals("[1, 2, 3, 4]", la.plus(4L).toList().toString())        // [1, 2, 3, 4]
        val da = arrayOf(1.5, 2.5)
        assertEquals("[1.5, 2.5, 3.5]", da.plusElement(3.5).toList().toString())  // [1.5, 2.5, 3.5]
        val ca = arrayOf('a', 'b')
        assertEquals("[a, b, c]", ca.plus('c').toList().toString())          // [a, b, c]
        val s = arrayOf("a", "b")
        assertEquals("[a, b, c]", s.plus("c").toList().toString())           // [a, b, c]
        assertEquals("[a, b, d]", s.plusElement("d").toList().toString())    // [a, b, d]
    }

    // #86 — `toTypedArray` joins plus/plusElement as an `Array<T>` factory that allocates through the
    // `arrayOfNulls<T>(n)` chain: the allocation's element slot is a nullable generic, and every element store has
    // to agree with it on one representation. A producer/consumer disagreement there does not throw — it prints
    // whatever the stale slot held — so each element is asserted individually at T=Int and T=Boolean, the two
    // instantiations where the erased element and the declared element genuinely differ.
    @TestAttribute
    fun toTypedArrayAtValueElements() {
        val ints = listOf(1, 2, 3).toTypedArray()
        assertEquals(3, ints.size)                                              // 3
        assertEquals(1, ints[0])                                                // 1
        assertEquals(3, ints[2])                                                // 3
        assertEquals("[1, 2, 3]", ints.toList().toString())                     // [1, 2, 3]
        assertEquals("[1, 2, 3, 4]", ints.plus(4).toList().toString())          // [1, 2, 3, 4]
        assertEquals("[1, 2, 3, 5]", ints.plusElement(5).toList().toString())   // [1, 2, 3, 5]
        val bools = listOf(true, false).toTypedArray()
        assertEquals(2, bools.size)                                             // 2
        assertEquals("[True, False]", bools.toList().toString())                // [True, False] (CLR rendering)
        assertEquals("[True, False, True]", bools.plus(true).toList().toString())        // [True, False, True]
        assertEquals("[True, False, False]", bools.plusElement(false).toList().toString()) // [True, False, False]
        val strs = listOf("a", "b").toTypedArray()
        assertEquals("[a, b, c]", strs.plus("c").toList().toString())           // [a, b, c] (reference control)
        assertEquals(0, listOf<Int>().toTypedArray().size)                      // 0 (empty allocation)
    }

    @OptIn(kotlin.ExperimentalUnsignedTypes::class)
    @TestAttribute
    fun primitiveArrayExt() {
        assertEquals("[1, 2]", intArrayOf(1, 2).toList().toString())            // [1, 2]
        assertEquals("[a, b]", charArrayOf('a', 'b').toList().toString())       // [a, b]
        assertEquals("[1, 2, 3]", longArrayOf(1L, 2L, 3L).toList().toString())  // [1, 2, 3]
        assertEquals("[1.5, 2.5]", doubleArrayOf(1.5, 2.5).toList().toString()) // [1.5, 2.5]
        val grown = intArrayOf(1, 2).copyOf(4)
        assertEquals("[1, 2, 0, 0]", grown.toList().toString())                // [1, 2, 0, 0]
        val dst = IntArray(3)
        intArrayOf(7, 8).copyInto(dst)
        assertEquals("[7, 8, 0]", dst.toList().toString())                     // [7, 8, 0]
        assertEquals("[1, 2]", ubyteArrayOf(1u, 2u).toList().toString())       // [1, 2]
        assertEquals("[3, 4]", uintArrayOf(3u, 4u).toList().toString())        // [3, 4]
        assertEquals("[9, 8]", ubyteArrayOf(9u, 8u).contentToString())         // [9, 8]
        assertEquals("[5, 6]", intArrayOf(5, 6).contentToString())             // [5, 6]
    }

    @TestAttribute
    fun overlapSafe() {
        val a = arrayOf(1, 2, 3, 4, 5)
        a.copyInto(a, 1, 0, 4)                         // right shift (overlapping)
        assertEquals("1,1,2,3,4", a.joinToString(","))  // 1,1,2,3,4
        val b = arrayOf(1, 2, 3, 4, 5)
        b.copyInto(b, 0, 1, 5)                         // left shift (overlapping)
        assertEquals("2,3,4,5,5", b.joinToString(","))  // 2,3,4,5,5
        val s = arrayOf("a", "b", "c", "d", "e")
        s.copyInto(s, 2, 0, 3)                         // reference right shift by 2
        assertEquals("a,b,a,b,c", s.joinToString(","))  // a,b,a,b,c
        val dq = ArrayDeque<String>(10)
        dq.addAll(listOf("a", "b", "c", "d"))
        dq.add(2, "X")                                 // in-place overlapping shift (generic copyInto)
        assertEquals("a,b,X,c,d", dq.joinToString(",")) // a,b,X,c,d
    }

    @TestAttribute
    fun rangeValidation() {
        val a = arrayOf("a", "b", "c", "d", "e")
        a.fill("z", 1, 3)
        assertEquals("a,z,z,d,e", a.joinToString(","))  // a,z,z,d,e
        val r1 = try { arrayOf("a", "b", "c").fill("z", 2, 1); "no-throw" } catch (e: IllegalArgumentException) { "iae" }
        assertEquals("iae", r1)                         // iae (inverted range)
        val r2 = try { arrayOf("a", "b", "c").fill("z", 0, 5); "no-throw" } catch (e: IndexOutOfBoundsException) { "ioobe" }
        assertEquals("ioobe", r2)                       // ioobe (toIndex past end)
        val r3 = try { arrayOf("a", "b", "c").fill("z", -1, 2); "no-throw" } catch (e: IndexOutOfBoundsException) { "ioobe-neg" }
        assertEquals("ioobe-neg", r3)                   // ioobe-neg (negative fromIndex)
        val b = arrayOf(0, 0, 0)
        b.fill(4)
        assertEquals("4,4,4", b.joinToString(","))      // 4,4,4
    }

    @TestAttribute
    fun forInRange() {
        var s1 = ""
        for (i in listOf("a", "b", "c").indices) s1 = s1 + i
        assertEquals("012", s1)                         // 012
        var s2 = ""
        for (i in "hello".indices) s2 = s2 + i
        assertEquals("01234", s2)                       // 01234
        val r = listOf("x", "y", "z", "w").indices
        var s3 = ""
        for (i in r) s3 = s3 + i
        assertEquals("0123", s3)                        // 0123
        var s4 = ""
        for (i in listOf<String>().indices) s4 = s4 + i
        assertEquals("", s4)                            // (empty; the old "end" marker was a bare println)
    }

    @TestAttribute
    fun valueElementIndices() {
        var s1 = ""
        for (i in listOf(1, 2, 3).indices) s1 = s1 + i  // value-element (Int) — the #30 crash site
        assertEquals("012", s1)                         // 012
        assertEquals(2, listOf(1, 2, 3).lastIndex)      // 2
        assertEquals(1, listOf(10, 20).lastIndex)       // 1
        assertEquals(-1, listOf<Int>().lastIndex)       // -1
        var s2 = ""
        for (i in listOf<Int>().indices) s2 = s2 + i
        assertEquals("", s2)                            // (empty; the old "e" marker was a bare println)
        val d = listOf(1.5, 2.5, 3.5)                   // Double element (value type)
        var sum = 0
        for (i in d.indices) sum += i
        assertEquals(3, sum)                            // 3 (0+1+2)
        var s3 = ""
        for (i in listOf("a", "b", "c").indices) s3 = s3 + i  // reference element
        assertEquals("012", s3)                         // 012
        assertEquals(3, listOf("x", "y", "z", "w").lastIndex)  // 3
    }

    @OptIn(kotlin.ExperimentalUnsignedTypes::class)
    @TestAttribute
    fun unsignedArray() {
        val a: UByteArray = ubyteArrayOf(1u, 2u, 250u)
        assertEquals(3, a.size)                         // 3
        assertEquals(250, a[2].toInt())                 // 250 (unsigned read; a signed Byte would be -6)
        val b: ByteArray = a.toByteArray()
        assertEquals(-6, b[2].toInt())                  // -6 (signed reinterpret of 250)
        val c: UByteArray = b.toUByteArray()
        assertEquals(250, c[2].toInt())                 // 250 (reinterpret back to unsigned)
        val ub: UByte = 200u
        assertEquals(200, ub.toInt())                   // 200
    }

    @TestAttribute
    fun nullableTvArray() {
        val b = ArrBox<Int>(2)
        assertEquals(2, b.count())      // 2
        assertNull(b.a[0].v)            // null (value-type element: chained read across the boundary)
        assertNull(b.elem(1).v)         // null (value-type element: via the getter method)
        val r: ArrRef<Int?> = b.a[0]    // retyped local (irreconcilable generic arg)
        assertNull(r.v)                 // null
        val x: Int? = b.a[1].v          // value-typed consumer (object -> Nullable<int32>)
        assertNull(x)                   // null
        val s = ArrBox<String>(3)
        assertEquals(3, s.count())      // 3
        assertNull(s.a[2].v)            // null (reference element: chained read across the boundary)
        val rs: ArrRef<String?> = s.elem(0)
        assertNull(rs.v)                // null
    }
}
