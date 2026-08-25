// Collections battery — migrates the list/set/iteration/collection-op family of cases/il-* onto the in-process
// NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value
// assertEquals/assertTrue/assertFalse is strictly stronger (typed, fails the exact broken contract) and
// self-documenting. Every value the old il_check (or the JVM-oracle differential, for the two PURE-only cases)
// asserted is preserved 1:1 (see the `// <expected>` comments). Interior `println`s that were part of a case's
// contract (loop order, iteration side effects) are captured into a log/list and asserted in order.
//
// The MAP-typed family (mapOf/groupBy/merge/mapValues/eachCount/map-destructure/…) lives in the sibling
// MapsTests battery; this file is list / set / iteration / collection-operation behavior.
//
// EXCLUDED from the collections family (real subject is elsewhere — kept in the bash lane):
//   il-groupvalues  -> Regex MatchResult.groupValues/destructured (text/regex family, not a collection)
//   il-setlocalbox  -> assigning a primitive Int into an `Any` slot (boxing/Any family, no collection)
//
// Coverage preserved (old case -> method):
//   il-coll        -> coll_linqOps          map/filter/take/drop/any/all/count/first/contains/reversed
//   il-coll2       -> coll2_foldJoin        fold -> Aggregate, joinToString -> String.Join
//   il-coll3       -> coll3_forEach         forEach enumerator loop + closure capture
//   il-collmore    -> collmore_ops          mapNotNull/flatMap/flatten/sum/average/indexOf + nullable pick
//   il-collops2    -> collops2_ops          partition/withIndex/associate/scan/runningFold/windowed/getOrElse
//   il-collrealkt  -> collrealkt_genExt     generic List/MutableList/Map extensions (indexer/size member access)
//   il-collrevview -> collrevview_variance  #100 reverse variance-collapse seam (readonly List into mutable slot)
//   il-hashset2    -> hashset2_capacityCtor (int,float) collection ctor -> capacity-only (int) ctor
//   il-iscoll      -> iscoll_isChecks       star-projected @ClrTypeAlias is-test -> non-generic BCL interface
//   il-iter        -> iter_operatorIterator user-defined `operator fun iterator`/hasNext/next
//   il-iterable    -> iterable_interfaces   user class implementing Kotlin Iterator<T>/Iterable<T>
//   il-listeq      -> listeq_structural     collection `==` is STRUCTURAL (List ordered / Set / Map entrywise)
//   il-listplus    -> listplus_plus         List + List / List + element (JVM-oracle differential)
//   il-mapfilter   -> mapfilter_mapFilter   map/filter routed to the real Kotlin body (List) vs LINQ (Array)
//   il-mutcoll     -> mutcoll_mutableOps    MutableList add/remove/clear/removeAt + generic ArrayList<R> build
//   il-mutset      -> mutset_setRemoveAt    MutableList.set/removeAt RETURN the previous/removed element
//   (#400)         -> readOnlyViewOfUserMutableCollection  a user class naming only the MUTABLE collection face
//                     still has the CLR read-only view, because bir2cir states the sibling interface
//
// Top-level names are unique within this single battery assembly (one project = one namespace); il-iter's IntBox
// is renamed IterIntBox because GenericsTests already owns `IntBox`, and the two `Countdown` classes are
// disambiguated (IterCountdown / IterableCountdown).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse

// ---- il-collrealkt : generic collection extensions exercising indexer/size member access -----------------------
fun <T> List<T>.crkFirstE(): T {
    if (size == 0) throw NoSuchElementException("List is empty.")
    return this[0]
}
fun <T> List<T>.crkLastE(): T = this[size - 1]
fun <T> List<T>.crkGetOrElseE(index: Int, defaultValue: (Int) -> T): T =
    if (index in 0 until size) this[index] else defaultValue(index)
fun <T> MutableList<T>.crkSwap01() { val t = this[0]; this[0] = this[1]; this[1] = t }
fun <K, V> Map<K, V>.crkValAt(k: K): V = this[k]!!

// ---- #287 : a null that the frontend cannot const-fold, for the nullable star-projected is-test ---------------
fun collNullSrc(): Any? = null

// #315: emitted Kotlin implementations use compiler-owned nominal collection identities alongside their BCL
// operational faces. Delegation keeps these probes about classifier identity rather than collection mechanics.
class CollectionIdentityOnly(private val elements: List<Int>) : Collection<Int> by elements
class SetIdentityOnly(private val elements: Set<Int>) : Set<Int> by elements
class MutableSetIdentityOnly(private val elements: MutableSet<Int>) : MutableSet<Int> by elements

// ---- il-collrevview : #100 H1 reverse variance-collapse seam -- make() returns the readonly List<Int> head ------
fun collRevMake(): List<Int> = listOf(1, 2)

// ---- il-iter : user-defined `operator fun iterator` -----------------------------------------------------------
class IterIntBoxIterator(val items: IntArray) {
    var idx = 0
    operator fun hasNext(): Boolean = idx < items.size
    operator fun next(): Int {
        val v = items[idx]
        idx = idx + 1
        return v
    }
}
class IterIntBox(val items: IntArray) {
    operator fun iterator(): IterIntBoxIterator = IterIntBoxIterator(items)
}
// iterator() returning an anonymous object implementing kotlin.collections.Iterator<T>.
class IterCountdown(val from: Int) {
    operator fun iterator(): Iterator<Int> = object : Iterator<Int> {
        var cur = from
        override fun hasNext(): Boolean = cur > 0
        override fun next(): Int {
            val v = cur
            cur = cur - 1
            return v
        }
    }
}

// ---- il-iterable : user class implementing Kotlin's Iterator<T>/Iterable<T> -----------------------------------
class IterableCountdown(var n: Int) : Iterator<Int> {
    override fun hasNext(): Boolean = n > 0
    override fun next(): Int { val r = n; n -= 1; return r }
}
class Range3 : Iterable<Int> {
    override fun iterator(): Iterator<Int> = IterableCountdown(3)
}


// ---- #400 : the READ-ONLY view of a user class that implements only the MUTABLE collection face ----------------
// Kotlin's MutableList<E> IS-A List<E>, so passing one of these into a `List<E>`/`Collection<E>` slot is a no-op in
// the source language. On the CLR it is a cast between IList<T> and IReadOnlyList<T>, two interfaces neither of
// which derives from the other, so it is total only because bir2cir states the read-only face on the emitted type.
// A class declaring ONLY the mutable Kotlin face is the shape that shows it: the BCL collections declare both
// faces themselves, and inheriting from AbstractMutableList would take the face from the stdlib base instead.
class CollUserList<E>(private val inner: MutableList<E> = mutableListOf()) : MutableList<E> {
    override val size: Int get() = inner.size
    override fun isEmpty(): Boolean = inner.isEmpty()
    override fun contains(element: E): Boolean = inner.contains(element)
    override fun iterator(): MutableIterator<E> = inner.iterator()
    override fun containsAll(elements: Collection<E>): Boolean = inner.containsAll(elements)
    override fun get(index: Int): E = inner[index]
    override fun indexOf(element: E): Int = inner.indexOf(element)
    override fun lastIndexOf(element: E): Int = inner.lastIndexOf(element)
    override fun listIterator(): MutableListIterator<E> = inner.listIterator()
    override fun listIterator(index: Int): MutableListIterator<E> = inner.listIterator(index)
    override fun subList(fromIndex: Int, toIndex: Int): MutableList<E> = inner.subList(fromIndex, toIndex)
    override fun add(element: E): Boolean = inner.add(element)
    override fun add(index: Int, element: E) { inner.add(index, element) }
    override fun addAll(index: Int, elements: Collection<E>): Boolean = inner.addAll(index, elements)
    override fun addAll(elements: Collection<E>): Boolean = inner.addAll(elements)
    override fun clear() { inner.clear() }
    override fun remove(element: E): Boolean = inner.remove(element)
    override fun removeAll(elements: Collection<E>): Boolean = inner.removeAll(elements)
    override fun removeAt(index: Int): E = inner.removeAt(index)
    override fun retainAll(elements: Collection<E>): Boolean = inner.retainAll(elements)
    override fun set(index: Int, element: E): E = inner.set(index, element)
}
// The bare mutable-collection face (ICollection<T>), whose read-only sibling is IReadOnlyCollection<T>.
class CollUserBag(private val inner: MutableList<String> = mutableListOf()) : MutableCollection<String> {
    override val size: Int get() = inner.size
    override fun isEmpty(): Boolean = inner.isEmpty()
    override fun contains(element: String): Boolean = inner.contains(element)
    override fun iterator(): MutableIterator<String> = inner.iterator()
    override fun containsAll(elements: Collection<String>): Boolean = inner.containsAll(elements)
    override fun add(element: String): Boolean = inner.add(element)
    override fun addAll(elements: Collection<String>): Boolean = inner.addAll(elements)
    override fun clear() { inner.clear() }
    override fun remove(element: String): Boolean = inner.remove(element)
    override fun removeAll(elements: Collection<String>): Boolean = inner.removeAll(elements)
    override fun retainAll(elements: Collection<String>): Boolean = inner.retainAll(elements)
}
// Read-only-typed consumers: `List<T>` lowers to IReadOnlyList<T>, `Collection<T>` to IReadOnlyCollection<T>.
fun collSumReadOnlyList(xs: List<Int>): Int {
    var total = 0
    for (i in 0 until xs.size) total += xs[i]
    return total
}
fun collJoinReadOnlyCollection(xs: Collection<String>): String {
    val sb = StringBuilder()
    for (x in xs) { sb.append(x); sb.append(";") }
    return sb.toString()
}

// ---- il-mutcoll : generic ArrayList<R> built by iterate + .add (the shape the real stdlib map/filter use) ------
fun <T, R> Iterable<T>.mapTo2(transform: (T) -> R): List<R> {
    val out = ArrayList<R>()
    for (item in this) out.add(transform(item))
    return out
}

class CollectionsTests {
    @TestAttribute
    fun linqOps() {
        val xs = listOf(1, 2, 3, 4, 5)
        assertEquals(5, xs.size)                        // 5
        assertEquals(5, xs.map { it * 2 }.size)         // 5
        assertEquals(3, xs.filter { it > 2 }.size)      // 3
        assertEquals(2, xs.take(2).size)                // 2
        assertEquals(3, xs.drop(2).size)                // 3
        assertTrue(xs.any { it > 4 })                   // True
        assertTrue(xs.all { it > 0 })                   // True
        assertEquals(3, xs.count { it > 2 })            // 3
        assertEquals(1, xs.first())                     // 1
        assertEquals(4, xs.first { it > 3 })            // 4
        assertTrue(xs.contains(3))                      // True
        assertEquals(5, xs.reversed().first())          // 5
    }

    @TestAttribute
    fun foldJoin() {
        val xs = listOf(1, 2, 3, 4)
        assertEquals(10, xs.fold(0) { acc, x -> acc + x })          // 10
        assertEquals("1-2-3-4", xs.joinToString("-"))               // 1-2-3-4
        assertEquals("1, 2, 3, 4", xs.joinToString())               // 1, 2, 3, 4
        assertEquals(100, xs.map { it * 10 }.fold(0) { a, b -> a + b }) // 100
    }

    // #287: every join path renders a NULL element as the four characters "null" — the contract `joinTo`/`joinToString`
    // inherit from `Appendable.append(CharSequence?)`. The shared `appendElement` reaches it through `element is
    // CharSequence?`, whose nullable type operand accepts null (NullableTests.nullableTypeOperandIsTest pins that);
    // when the operand answered false the frontend's else-branch smart-cast dereferenced the null and the whole join
    // threw. Swept across the receiver families and over first/middle/last null positions.
    @TestAttribute
    fun joinNullElements() {
        assertEquals("null, null", arrayOfNulls<String>(2).joinToString())                              // the issue repro
        assertEquals("null, b, null, d, null", arrayOf<String?>(null, "b", null, "d", null).joinToString())
        assertEquals("null-1-s", arrayOf<Any?>(null, 1, "s").joinToString("-"))                         // generic array
        assertEquals("null, x, null", listOf<String?>(null, "x", null).joinToString())                  // list
        assertEquals("null, 2", listOf<Int?>(null, 2).joinToString())                                   // nullable VALUE element
        // Sequence receiver via Iterable.asSequence; the formerly crashing array-backed surfaces are pinned by
        // SequenceOperationTests.arrayBackedSequenceIteration (#284).
        assertEquals("null, y", listOf<String?>(null, "y").asSequence().joinToString())
        assertEquals("null, y", listOf<String?>(null, "y").asSequence().map { it }.joinToString())
        assertEquals("N, z", listOf<String?>(null, "z").joinToString { it ?: "N" })                     // transform wins
        assertEquals("null, null, ...", listOf<String?>(null, null, null).joinToString(limit = 2))      // limit + truncated
        assertEquals("ab", listOf('a', 'b').joinToString(""))                                           // Char branch intact
        val sb = StringBuilder()
        listOf<Int?>(null, 1, null).joinTo(sb, "|", "<", ">")
        assertEquals("<null|1|null>", sb.toString())                                                    // joinTo + affixes
    }

    @TestAttribute
    fun forEach() {
        val xs = listOf(10, 20, 30)
        var sum = 0
        xs.forEach { sum = sum + it }
        assertEquals(60, sum)                           // 60
        var n = 0
        xs.map { it / 10 }.forEach { n = n + it }
        assertEquals(6, n)                              // 6
    }

    @TestAttribute
    fun collectionTransformations() {
        val xs = listOf(1, 2, 3, 4, 5)
        assertEquals("20,40", xs.mapNotNull { if (it % 2 == 0) it * 10 else null }.joinToString(",")) // 20,40
        assertEquals("1,10,2,20,3,30,4,40,5,50", xs.flatMap { listOf(it, it * 10) }.joinToString(",")) // 1,10,...,5,50

        val nested = listOf(listOf(1, 2), listOf(3), listOf(4, 5))   // listOf(3) is List<Int>, not List<Any>
        assertEquals("1,2,3,4,5", nested.flatten().joinToString(","))  // 1,2,3,4,5
        assertEquals(15, nested.flatten().sum())                      // 15

        fun pick(n: Int): Int? = if (n > 0) n * 2 else null
        assertEquals(14, pick(7) ?: -1)                               // 14
        assertEquals(-1, pick(-1) ?: -1)                              // -1

        assertEquals(3.0, xs.average())                               // 3  (Double 3.0, CLR prints "3")
        assertEquals(3, xs.indexOf(4))                                // 3
    }

    @TestAttribute
    fun partitionIndexAndAssociate() {
        val xs = listOf(1, 2, 3, 4, 5, 6)
        val (even, odd) = xs.partition { it % 2 == 0 }
        assertEquals("2,4,6 | 1,3,5", "${even.joinToString(",")} | ${odd.joinToString(",")}")  // 2,4,6 | 1,3,5

        val idx = StringBuilder()
        for ((i, v) in listOf("a", "b", "c").withIndex()) idx.append("$i:$v ")
        assertEquals("0:a 1:b 2:c ", idx.toString())                  // 0:a 1:b 2:c  (trailing space)

        val m = listOf("x", "yy", "zzz").associate { it to it.length }
        assertEquals("1,2,3", "${m["x"]},${m["yy"]},${m["zzz"]}")     // 1,2,3

        assertEquals("0,1,3,6,10", listOf(1, 2, 3, 4).scan(0) { a, b -> a + b }.joinToString(","))          // 0,1,3,6,10
        assertEquals("100,101,103,106,110", listOf(1, 2, 3, 4).runningFold(100) { a, b -> a + b }.joinToString(",")) // 100,...,110
        assertEquals("6,9,12", listOf(1, 2, 3, 4, 5).windowed(3).map { it.sum() }.joinToString(","))         // 6,9,12

        assertEquals(3, xs.getOrElse(2) { -1 })                       // 3
        assertEquals(-99, xs.getOrElse(99) { it * -1 })               // -99
    }

    @TestAttribute
    fun genExt() {
        val xs = listOf(10, 20, 30)
        assertEquals(10, xs.crkFirstE())                              // 10
        assertEquals(30, xs.crkLastE())                               // 30
        assertEquals(500, xs.crkGetOrElseE(5) { it * 100 })           // 500
        val m = mutableListOf("a", "b", "c")
        m.crkSwap01()
        assertEquals("b,a,c", m.joinToString(","))                    // b,a,c
        val d = mapOf(1 to "one", 2 to "two")
        assertEquals("two", d.crkValAt(2))                            // two
    }

    @TestAttribute
    fun variance() {
        // #100 H1: a readonly-faced List<Int> flowing into a same-family collapsed MUTABLE type-arg slot.
        val p = Pair(collRevMake(), 3)
        assertEquals("([1, 2], 3)", p.toString())                     // ([1, 2], 3)
        val m = mutableMapOf<String, List<Int>>()
        m["k"] = collRevMake()
        assertEquals("{k=[1, 2]}", m.toString())                      // {k=[1, 2]}
    }

    @TestAttribute
    fun capacityCtor() {
        // (initialCapacity, loadFactor) has no BCL (int,float) equivalent -> loadFactor dropped, capacity-only ctor.
        val s = HashSet<Int>(16, 0.75f)
        s.add(1); s.add(2); s.add(2)
        assertEquals(2, s.size)                         // 2
        val ls = LinkedHashSet<String>(8, 0.5f)
        ls.add("a"); ls.add("b")
        assertEquals(2, ls.size)                        // 2
        val m = HashMap<Int, String>(8, 0.5f)
        m[1] = "x"
        assertEquals(1, m.size)                         // 1
        val lm = LinkedHashMap<Int, Int>(4, 0.9f)
        lm[1] = 10
        assertEquals(1, lm.size)                        // 1
    }

    @TestAttribute
    fun isChecks() {
        val c: Any = listOf(1, 2, 3)
        val m: Any = mapOf(1 to 2, 3 to 4)
        assertTrue(c is Collection<*>)                  // True
        assertTrue(c is List<*>)                        // True
        assertTrue(c is Iterable<*>)                    // True
        assertTrue(m is Map<*, *>)                      // True
        val notColl: Any = 5
        val notList: Any = "hi"
        assertFalse(notColl is Collection<*>)           // False
        assertFalse(notList is List<*>)                 // False
        // #287: the NULLABLE star-projected test names the same classifier, so it must reach the same non-generic
        // BCL facade — the reified `IReadOnlyCollection<object>`/`IDictionary<object,object>` it would otherwise
        // hit is not implemented by a value-arg List<int>/Dictionary<int,int>, so the test would answer false.
        assertTrue(c is Collection<*>?)                 // True
        assertTrue(c is List<*>?)                       // True
        assertTrue(m is Map<*, *>?)                     // True
        assertFalse(notColl is Collection<*>?)          // False
        val nothing: Any? = collNullSrc()
        assertTrue(nothing is Collection<*>?)           // True   null IS an instance of the nullable type
    }

    // The INVARIANT this battery owes `x is T?`: for a non-null receiver it answers exactly what `x is T` answers,
    // and for null it answers true. That has to hold even where `is T` is itself wrong — the two spellings must never
    // disagree, or a `?` silently changes which branch a `when` takes.
    //
    // #315: the operational aliases still use the BCL collection surfaces, but emitted Kotlin implementations carry
    // independent compiler-owned classifier identities. BCL-backed values are classified by the generic faces they
    // actually implement; dictionaries and arrays are excluded from Collection despite their CLR collection shape.
    @TestAttribute
    fun starProjectedSetIdentity() {
        val setInt: Any = setOf(1, 2)
        val setStr: Any = setOf("a")
        val mutSetStr: Any = mutableSetOf("a")
        val listInt: Any = listOf(1)
        val listStr: Any = listOf("a")
        val mapInt: Any = mapOf(1 to 2)

        assertTrue(setInt is Set<*>)
        assertTrue(setStr is Set<*>)
        assertTrue(mutSetStr is Set<*>)
        assertFalse(listInt is Set<*>)
        assertFalse(listStr is Set<*>)
        assertFalse(mapInt is Set<*>)
        assertTrue(mutSetStr is MutableSet<*>)

        // The `?` spelling agrees with the plain one on every non-null receiver, and adds null. This is what #287
        // guarantees, and it holds regardless of the wrongness above.
        assertEquals(setInt is Set<*>, setInt is Set<*>?)
        assertEquals(setStr is Set<*>, setStr is Set<*>?)
        assertEquals(listInt is Set<*>, listInt is Set<*>?)
        assertEquals(listStr is Set<*>, listStr is Set<*>?)
        assertEquals(mutSetStr is MutableSet<*>, mutSetStr is MutableSet<*>?)
        assertTrue(collNullSrc() is Set<*>?)            // True   null IS an instance of the nullable type
        assertFalse(collNullSrc() is Set<*>)            // False  the non-null spelling still rejects null
        assertTrue(collNullSrc() !is Set<*>)            // True   the `!is` twin

        assertTrue(setInt is Collection<*>)
        assertTrue(setStr is Collection<*>)
        assertFalse(mapInt is Collection<*>)
        assertFalse((arrayOf(1) as Any) is Collection<*>)
        assertEquals(setInt is Collection<*>, setInt is Collection<*>?)
        assertEquals(mapInt is Collection<*>, mapInt is Collection<*>?)

        val userCollection: Any = CollectionIdentityOnly(listOf(1))
        val userSet: Any = SetIdentityOnly(setOf(1))
        val userMutableSet: Any = MutableSetIdentityOnly(mutableSetOf(1))
        assertTrue(userCollection is Collection<*>)
        assertFalse(userCollection is Set<*>)
        assertTrue(userSet is Collection<*>)
        assertTrue(userSet is Set<*>)
        assertFalse(userSet is MutableSet<*>)
        assertTrue(userMutableSet is Collection<*>)
        assertTrue(userMutableSet is Set<*>)
        assertTrue(userMutableSet is MutableSet<*>)

        // A successful smart cast must keep member access valid for a value-element HashSet, while a safe cast of a
        // list must fail at the Kotlin classifier boundary rather than accepting the shared collection face.
        assertEquals(2, if (setInt is Set<*>) setInt.size else -1)
        assertTrue((setInt as? Set<*>) != null)
        assertTrue((listInt as? Set<*>) == null)

        // List and Map have exact non-generic BCL twins.
        assertTrue(listInt is List<*>)                  // True
        assertFalse(setInt is List<*>)                  // False
        assertTrue(mapInt is Map<*, *>)                 // True
        assertFalse(listInt is Map<*, *>)               // False
    }

    @TestAttribute
    fun operatorIterator() {
        val box = IterIntBox(intArrayOf(10, 20, 30))
        val log = mutableListOf<String>()
        var sum = 0
        for (x in box) {
            log.add("x=$x")
            sum = sum + x
        }
        log.add("sum = $sum")
        var acc = 0
        for (n in IterCountdown(3)) {
            log.add("n=$n")
            acc = acc + n
        }
        log.add("acc = $acc")
        // x=10 / x=20 / x=30 / sum = 60 / n=3 / n=2 / n=1 / acc = 6
        assertEquals("x=10|x=20|x=30|sum = 60|n=3|n=2|n=1|acc = 6", log.joinToString("|"))
        assertEquals(60, sum)                           // 60
        assertEquals(6, acc)                            // 6
    }

    @TestAttribute
    fun interfaces() {
        val sb = StringBuilder()
        for (x in Range3()) sb.append(x)                // 321
        assertEquals("321", sb.toString())              // 321
        val it = Range3().iterator()                    // explicit first-class Kotlin Iterator
        var s = 0
        while (it.hasNext()) s += it.next()
        assertEquals(6, s)                              // 6
        var c = 0
        for (x in Range3()) c += x                      // for-loop again (fresh iterator)
        assertEquals(6, c)                              // 6
    }

    @TestAttribute
    fun structural() {
        // Kotlin `==` on collections is STRUCTURAL (List ordered, Set unordered, Map entrywise).
        assertTrue(listOf(7, 8) == listOf(7, 8))                     // True
        assertFalse(listOf(7, 8) == listOf(8, 7))                    // False (order)
        assertTrue(setOf(1, 2) == setOf(2, 1))                       // True  (unordered)
        assertTrue(mapOf(1 to 2, 3 to 4) == mapOf(3 to 4, 1 to 2))   // True
        assertTrue(listOf("a", "b") == listOf("a", "b"))            // True
        assertFalse(listOf(1) == setOf(1))                           // False (List vs Set)
        assertFalse(listOf(1, 2) != listOf(1, 2))                    // False
        val a = listOf(1, 2, 3)
        assertTrue(a == a)                                           // True
        assertFalse(mapOf("x" to 1) == mapOf("x" to 2))             // False (value)
    }

    @TestAttribute
    fun plus() {
        assertEquals("[1, 2, 3, 4]", (listOf(1, 2) + listOf(3, 4)).toString())   // [1, 2, 3, 4]
        assertEquals("[1, 2, 3]", (listOf(1, 2) + 3).toString())                 // [1, 2, 3]
        assertEquals("[a, b, c]", (listOf("a", "b") + listOf("c")).toString())   // [a, b, c]
    }

    @TestAttribute
    fun mapFilter() {
        val xs = listOf(1, 2, 3, 4, 5)
        assertEquals("10,20,30,40,50", xs.map { it * 10 }.joinToString(","))             // collection map -> real Kotlin
        assertEquals("2,4", xs.filter { it % 2 == 0 }.joinToString(","))                 // collection filter -> real Kotlin
        assertEquals("4,5,6", xs.map { it + 1 }.filter { it > 3 }.joinToString(","))     // chained
        assertEquals("100,200,300", arrayOf(1, 2, 3).map { it * 100 }.joinToString(",")) // array map -> LINQ
        assertEquals("2,4,6", setOf(1, 2, 2, 3).map { it * 2 }.joinToString(","))        // set map -> real Kotlin
    }

    @TestAttribute
    fun mutableOps() {
        val m = mutableListOf(1, 2, 3)
        m.add(4)
        m.removeAt(0)
        assertEquals("2,3,4", m.joinToString(","))      // 2,3,4
        m.remove(3)
        assertEquals("2,4", m.joinToString(","))        // 2,4
        assertEquals(2, m.size)                         // 2
        m.clear()
        assertEquals(0, m.size)                         // 0
        assertEquals("11,22,33", listOf(1, 2, 3).mapTo2 { it * 11 }.joinToString(",")) // 11,22,33
    }

    @TestAttribute
    fun readOnlyViewOfUserMutableCollection() {
        // A user class that names only the MUTABLE Kotlin face still has the CLR read-only view, because bir2cir
        // states the sibling interface on the emitted type (#400). Without it these calls would be an
        // InvalidCastException at the argument cast, or unverifiable IL.
        val list = CollUserList<Int>()
        list.add(1); list.add(2); list.add(3)
        assertEquals(6, collSumReadOnlyList(list))              // IList<int32> value into an IReadOnlyList<int32> slot
        val asList: List<Int> = list                            // and held in a read-only-typed local
        assertEquals(3, asList.size)
        assertEquals(1, asList[0])
        assertEquals(6, collSumReadOnlyList(asList))

        val bag = CollUserBag()
        bag.add("p"); bag.add("q")
        assertEquals("p;q;", collJoinReadOnlyCollection(bag))   // ICollection<string> into an IReadOnlyCollection<string> slot
        val asColl: Collection<String> = bag
        assertEquals(2, asColl.size)
        assertEquals("p;q;", collJoinReadOnlyCollection(asColl))
    }

    @TestAttribute
    fun setRemoveAt() {
        // MutableList.set / removeAt RETURN the previous/removed element (Kotlin), routed to clrListSet/clrListRemoveAt.
        val l = mutableListOf(10, 20, 30)
        val old = l.set(1, 99)
        assertEquals(20, old)                           // 20
        assertEquals("10,99,30", l.joinToString(","))   // 10,99,30
        val rm = l.removeAt(0)
        assertEquals(10, rm)                            // 10
        assertEquals("99,30", l.joinToString(","))      // 99,30
    }
}
