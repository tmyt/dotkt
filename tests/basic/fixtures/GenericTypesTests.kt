// Generic-types battery — the reified-CLR-generics MEMBER/TYPE anchoring family: a generic class inheriting a
// generic base (base-ctor targets the CONSTRUCTED base), generic secondary-constructor delegation (sibling ctor via
// the self-instantiation), an inherited generic-base method on a non-generic subclass + a self-bounded generic,
// cross-assembly static on a generic stdlib type (kotlin.Result), non-inlined generic collection building, a
// function-local class capturing the enclosing type parameter, a raw @ClrField access whose owner is a generic type
// (all four anchoring axes), an object/SAM expression capturing the enclosing type parameter, a generic factory, and
// constructing an EXTERNAL generic instantiated over a free type variable. Migrates that family of cases/il-* onto the
// in-process NUnit suite: each old case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value
// assertEquals/assertTrue/assertNull is strictly stronger (typed) than the old text diff. Every asserted value is
// preserved 1:1 (see the `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   il-genbase       -> genbase_genericBaseInheritance   `D<T> : Base<T>()` base-ctor targets the CONSTRUCTED base (cold-core SequenceBuilderIterator shape)
//   il-genctor       -> genctor_secondaryCtorDelegation  #? generic `constructor(...) : this(...)` via the self-instantiation C<T> (value + ref + two-type-param)
//   il-geninherit    -> geninherit_inheritedBaseMethod   #84-I inherited generic-base method on a non-generic subclass + self-bounded `Segment<S : Segment<S>>`
//   il-genstatic     -> genstatic_genericStdlibStatic    cross-assembly static on generic `kotlin.Result<T>` anchored onto the constructed instantiation
//   il-gencolladd    -> gencolladd_genericCollectionAdd  non-inlined generic .map/.add (clrCollAdd -> ICollection<!!T>.Count) + non-generic .map
//   il-genlocalclass -> genlocalclass_capturingLocal     #69 function-local class capturing an enclosing TYPE PARAMETER lifted generically
//   il-genfield      -> genfield_genericFieldAnchoring    #91 raw @ClrField on a generic owner anchored onto the CONSTRUCTED instantiation (4 axes)
//   il-objgen        -> objgen_capturingObjectLiteral    object-literal / fun-interface (SAM) capturing the enclosing type parameter
//   il-gfac          -> gfac_genericFactory              generic factory keeps State<T> constructed (gp:T), not the open type
//   il-genextnew     -> genextnew_externalGenericNew     #123 `new AtomicReference<T>(v)` over a FREE type-var (TypeBuilderInstantiation ctor re-anchor)
//
// Top-level names are unique within this single battery assembly (one project = one namespace) and case-prefixed
// (Gb/Gc/Gi/Gca/Glc/Gf/Og/Gfac/GenAtomic...) to avoid clashing with sibling batteries and stdlib names; the sole
// exception is `annotation class ClrField`, which kotc recognizes by SHORT NAME to emit a raw field (so it must stay
// literally `ClrField`).
//
// @file:OptIn — il-genextnew constructs kotlin.concurrent.atomics.AtomicReference (still an experimental API); the
// file-level opt-in covers its declaration/ctor-param/return-type positions (a per-fn @OptIn does not reach the
// signature type), scoped to this battery file only.
@file:OptIn(ExperimentalAtomicApi::class)

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsNull as assertNull
import kotlin.concurrent.atomics.AtomicReference
import kotlin.concurrent.atomics.ExperimentalAtomicApi

// ---- il-genbase : a generic class inheriting a generic base instantiated over its OWN type parameter ------------
open class GbBase<T>(val x: T) {
    open fun show(): T = x
}
class GbD<T>(v: T) : GbBase<T>(v) {
    fun twice(): String = "${show()}/${x}"
}

// ---- il-genctor : generic secondary-constructor delegation via the self-instantiation C<T> ----------------------
class GcRing<T>(val buffer: Array<Any?>, val filled: Int) {
    constructor(capacity: Int) : this(arrayOfNulls<Any?>(capacity), 0)
    fun cap(): Int = buffer.size
}
class GcPair2<A, B>(val a: Any?, val b: Any?, val tag: Int) {
    constructor(a: Any?, b: Any?) : this(a, b, 7)
    fun tagged(): Int = tag
}

// ---- il-geninherit : inherited generic-base method on a non-generic subclass + a self-bounded generic -----------
open class GiHolder<T>(val v: T) { fun get(): T = v }
class GiIntHolder(v: Int) : GiHolder<Int>(v)
abstract class GiSegment<S : GiSegment<S>> {
    var next: S? = null
    fun link(n: S) { next = n }
}
class GiSeg : GiSegment<GiSeg>()

// Two generic MethodDefs on one local generic owner have the same name and method arity but swap owner/method-generic
// parameter positions. The selected overload is intentionally the non-last declaration in each class. Borrowing the
// last name-keyed MethodBuilder's substituted parameter vector would swap a value slot with a reference slot, making
// the emitted call unverifiable even though its MethodSpec token still names the right declaration.
class GiGenericMethodOwnerA<T> {
    fun <U> select(left: T, right: List<U>): String = "owner:$left/${right.size}"
    fun <U> select(left: List<U>, right: T): String = "list:${left.size}/$right"
}
class GiGenericMethodOwnerB<T> {
    fun <U> select(left: List<U>, right: T): String = "list:${left.size}/$right"
    fun <U> select(left: T, right: List<U>): String = "owner:$left/${right.size}"
}

// ---- il-gencolladd : non-inlined generic collection building (.map/.add) + a non-generic .map ------------------
fun <T> gcaMapSelf(xs: Array<T>): List<T> = xs.map { it }
fun <T> gcaBuildAndCount(xs: Array<T>): Int {
    val out = mutableListOf<T>()
    var added = 0
    for (x in xs) { if (out.add(x)) added++ }   // add's Boolean return -> clrCollAdd -> c.size (ICollection<!!T>.Count)
    return added + out.size
}
fun gcaNonGenMap(n: Int): List<String> = (0 until n).map { "v$it" }

// ---- il-genlocalclass : a function-local class capturing an enclosing TYPE PARAMETER (lifted generically) -------
fun <T> glcFirstBox(t: T): T {
    class L(val label: String) { val x: T = t }
    val l = L("box")
    return l.x
}
fun <T> glcRoundTrip(a: T, b: T): String {
    class Cell {
        var value: T = a
        fun swap(n: T): T { val old = value; value = n; return old }
    }
    val c = Cell()
    val old = c.swap(b)
    return "$old->${c.value}"
}

// ---- il-genfield : a raw @ClrField access whose owner is a GENERIC type (anchored onto the instantiation) -------
annotation class ClrField   // recognized by SHORT NAME -> @ClrField => a raw `field` node (no property getter)
open class GfBase<T>(v: T) {
    @ClrField var slot: T = v            // plain CLR field on a GENERIC base
}
class GfCell<T>(v: T) {
    @ClrField var item: T = v            // plain CLR field on a GENERIC type
    fun read(): T = item                 // (a) self-instantiation own field via `this`
    fun replace(x: T): T { val old = item; item = x; return old }
}
class GfWrap<T>(v: T) : GfBase<T>(v) {
    fun peek(): T = slot                 // (b) self-instantiation INHERITED generic-base field via `this` (#91 core)
    fun put(x: T) { slot = x }
}
class GfIntBox(v: Int) : GfBase<Int>(v)  // (c) NON-generic subclass of a generic base
class GfSub<T>(v: T) : GfBase<T>(v)      // (d) GENERIC subclass of a generic base

// ---- il-objgen : an object-literal / fun-interface (SAM) capturing the enclosing type parameter ----------------
interface OgBox<T> { fun get(): T }
fun <T> ogBoxed(v: T): OgBox<T> = object : OgBox<T> { override fun get(): T = v }
fun interface OgProducer<T> { fun make(): T }
fun <T> ogProduce(v: T): OgProducer<T> = OgProducer { v }

// ---- il-gfac : a generic factory keeps State<T> constructed (gp:T), not the open type --------------------------
class GfacState<T>(val value: T)
fun <T> gfacState(i: T): GfacState<T> = GfacState(i)

// ---- il-genextnew : constructing an EXTERNAL generic instantiated over a FREE type variable --------------------
class GenAtomicRef<T>(val a: AtomicReference<T>)
fun <T> genAtomic(v: T): GenAtomicRef<T> = GenAtomicRef(AtomicReference(v))
fun <T> genBoxed(v: T): AtomicReference<T> = AtomicReference(v)   // DIRECT free-T external new (external branch alone)

class GenericTypesTests {
    @TestAttribute
    fun genericBaseInheritance() {
        val d = GbD(42)
        assertEquals(42, d.x)             // 42
        assertEquals(42, d.show())        // 42
        assertEquals("42/42", d.twice())  // 42/42
        val s = GbD("hi")
        assertEquals("hi", s.x)           // hi
    }

    @TestAttribute
    fun secondaryCtorDelegation() {
        val ri = GcRing<Int>(3)           // value-type instantiation via delegating ctor
        val rs = GcRing<String>(5)        // reference-type instantiation via delegating ctor
        assertEquals(3, ri.cap())         // 3
        assertEquals(0, ri.filled)        // 0   (-> "3,0")
        assertEquals(5, rs.cap())         // 5
        assertEquals(0, rs.filled)        // 0   (-> "5,0")
        val p = GcPair2<Int, String>(1, "x")  // two-type-param generic, delegating ctor
        assertEquals(7, p.tagged())       // 7
    }

    @TestAttribute
    fun inheritedBaseMethod() {
        assertEquals(42, GiIntHolder(42).get())  // inherited generic-base method on a non-generic subclass -> 42
        val a = GiSeg(); val b = GiSeg()
        a.link(b)
        assertTrue(a.next === b)          // self-bounded generic field access -> true
        assertNull(b.next)               // true (b.next == null)
        assertEquals("owner:7/2", GiGenericMethodOwnerA<Int>().select<String>(7, listOf("a", "b")))
        assertEquals("list:2/7", GiGenericMethodOwnerB<Int>().select<String>(listOf("a", "b"), 7))
    }

    @TestAttribute
    fun genericStdlibStatic() {
        val ok: Result<Int> = Result.success(42)
        assertEquals(42, ok.getOrNull())            // 42
        assertTrue(ok.isSuccess)                    // true
        val bad: Result<Int> = Result.failure(RuntimeException("boom"))
        assertTrue(bad.isFailure)                   // true
        assertEquals("boom", bad.exceptionOrNull()?.message)  // boom
        val s: Result<String> = Result.success("hi")
        assertEquals("hi", s.getOrNull())           // hi
    }

    @TestAttribute
    fun genericCollectionAdd() {
        val m = gcaMapSelf(arrayOf("a", "b", "c"))
        assertEquals("a,b,c", m.joinToString(","))  // a,b,c
        assertEquals(3, m.size)                      // 3
        assertEquals(4, gcaBuildAndCount(arrayOf(10, 20)))  // 2 + 2 = 4
        val ng = gcaNonGenMap(3)
        assertEquals("v0,v1,v2", ng.joinToString(","))  // v0,v1,v2
        assertEquals(3, ng.size)                     // 3
    }

    @TestAttribute
    fun capturingLocal() {
        assertEquals(42, glcFirstBox(42))            // 42   (T = Int, a value type)
        assertEquals("hi", glcFirstBox("hi"))        // hi   (T = String, a ref type)
        assertEquals("1->2", glcRoundTrip(1, 2))     // 1->2
        assertEquals("a->b", glcRoundTrip("a", "b")) // a->b
    }

    @TestAttribute
    fun genericFieldAnchoring() {
        val c = GfCell(41)
        assertEquals(41, c.read())        // 41
        assertEquals(41, c.replace(42))   // 41  (old)
        assertEquals(42, c.read())        // 42
        val w = GfWrap(100)
        assertEquals(100, w.peek())       // 100
        w.put(101)
        assertEquals(101, w.peek())       // 101
        val ib = GfIntBox(7)              // (c) constructed receiver, non-generic subclass
        assertEquals(7, ib.slot)          // 7
        ib.slot = 8
        assertEquals(8, ib.slot)          // 8
        val s = GfSub("hi")               // (d) constructed receiver, generic subclass
        assertEquals("hi", s.slot)        // hi
        s.slot = "bye"
        assertEquals("bye", s.slot)       // bye
    }

    @TestAttribute
    fun capturingObjectLiteral() {
        assertEquals(42, ogBoxed(42).get())        // 42
        assertEquals("hi", ogBoxed("hi").get())    // hi
        assertEquals(7, ogProduce(7).make())       // 7
        assertEquals("ok", ogProduce("ok").make()) // ok
    }

    @TestAttribute
    fun genericFactory() {
        assertEquals(42, gfacState(42).value)      // 42
        assertEquals("hi", gfacState("hi").value)  // hi
    }

    @TestAttribute
    fun externalGenericNew() {
        assertEquals(5, genAtomic(5).a.load())      // 5   (value-type free T through the wrapper)
        assertEquals("hi", genAtomic("hi").a.load()) // hi  (ref-type free T through the wrapper)
        assertEquals(42, genBoxed(42).load())        // 42  (bare external new over free T)
        assertEquals("yo", genBoxed("yo").load())    // yo
    }
}
