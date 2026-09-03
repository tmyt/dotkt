// Nullable / null-safety battery — migrates the nullable-reference / nullable-value / safe-call / elvis / `!!` /
// not-null-assertion / nullable-primitive family of cases/il-* onto the in-process NUnit suite. Old stdout-golden
// cases are grouped by compiler shape into typed assertEquals/assertNull methods. Exception cases (`!!` on null)
// become the proven try/catch-sentinel pattern (the catch clause pins the EXACT exception type; StringsTests uses
// the same shape for NumberFormatException). The side-effecting `println` in the try/finally probe is captured into a log
// list and asserted in order.
//
// EXCLUDED from this family (matched the grep prefix but the real subject is FLOAT / IEEE behavior, not nullability
// — kept in the bash lane for a future float battery):
//   il-nan     -> Double/Float NaN comparisons + infinities (any comparison with NaN is false)  — IEEE-float family
//   il-nancmp  -> float `<=`/`>=` unordered-inverted CIL compares (cgt.un/clt.un)                 — IEEE-float family
//   il-negzero -> Double/Float total order (-0.0 < 0.0, NaN largest & NaN==NaN) boxed vs IEEE     — IEEE-float family
//
// Coverage preserved (old case -> method):
//   il-null                  -> null_elvisSafeCallBang         elvis ?:, safe-call ?., not-null !!, String.length
//   il-nullable-generic-list -> nullable_genericListErasure    #28 List<T?> object-erased interface at every member read
//   il-nullableprim          -> nullableprim_valueTypeUnwrap   C1 value-nullable smart-cast must UNWRAP Nullable<T>.Value
//   il-nullbang              -> nullbang_notNullAssertion       #56/#118 `!!` value-type (Int/Long/Double/Byte/UInt/UByte)
//                               nullbang_unsignedSafeCallElvisCast  #118/#126 unsigned SAFE_CALL/ELVIS/`as?`/if-else join
//                               nullbang_referenceEagerThrow    #115 reference `!!` throws NPE EAGERLY (stored/discarded)
//   il-nullcollarg           -> nullcollarg_nullableInner       #100-H3 nullable-inner collection type-arg collapses V
//   il-nullcs                -> nullcs_stringIntoCharSequence    #156 nullable String into a CharSequence?-receiver slot
//   il-reqnn                 -> reqnn_requireCheckNotNull        requireNotNull / checkNotNull (reference + value)
//   il-safecallnv            -> safecallnv_safeCallNullableValue A5 `a?.member` value-type result; receiver once, unwrap
//   il-trynullable           -> trynullable_returnThroughFinally nullable Int? return through try/finally; finally runs
//
// nullableGenericSurfaceAtValueTypes is not migrated from a case: it is the #86 value-instantiation armor for the
// same-compilation `T?` DECLARATION surface (see the comment on the method).
//
// Top-level names are unique within this single battery assembly (one project = one namespace) and prefixed
// (`null`/`nv`/`tryNull`/`nullcs`/`ng`) to avoid clashing with sibling batteries and stdlib names.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsNull as assertNull
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext
import kotlin.coroutines.startCoroutine

// ---- #86 : the same-compilation `T?` DECLARATION surface at a VALUE instantiation ------------------------------
// A slot's physical type is a function of its DECLARED type, so each `T?` position has to hold a genuine null at a
// value instantiation and re-narrow at the typed read. The existing coverage of this surface is entirely T=String,
// where the whole family is invisible: a bare `T?` slot is trivially sound for a reference type.
//
// Every position is driven at a value instantiation, carrying a NULL as well as a value — the whole surface, since
// `Nullable(Tv)` is now `System.Object` everywhere and each of these used to fail JIT verification at T=Int.
fun <T> ngPick(x: T?, d: T): T = x ?: d             // `T?` PARAM
fun <T> ngFirstOrNull(xs: List<T>): T? = if (xs.isEmpty()) null else xs[0]   // `T?` RETURN
fun <T> ngRoundTrip(x: T?): T? {                    // `T?` param -> `T?` body-local -> `T?` return
    var local: T? = null
    local = x
    return local
}
class NgCell<T>(private val slot: T?) {             // `T?` CTOR PARAM + backing field + property
    val stored: T? get() = slot
    fun orElse(d: T): T = slot ?: d
}
open class NgBase<T>(val held: T?)                  // a ctor DELEGATION target whose param is erased
class NgDerived(y: Int?) : NgBase<Int>(y)           // `Base<Int>(y)` hands a Nullable<int32> to an `object` slot
// The inherited protected-property use axis is resolved only after bir2cir binds the call from the derived receiver
// to its declaring generic base. The declaration `Array<T?>` is physically `object[]` under #86, while this concrete
// reference instantiation observes the original `String[]`; CIR must state the checked projection between them.
open class NgProtectedArrayBase<T>(protected val values: Array<T?>?)
class NgProtectedArrayText(values: Array<String?>) : NgProtectedArrayBase<String>(values) {
    private fun invoke(block: () -> Unit) = block()

    fun snapshot(): Array<String?> {
        val result: Array<String?>? = values
        return result!!
    }

    fun capturedSnapshot(): Array<String?> {
        var result: Array<String?>? = null
        invoke { result = values }
        return result!!
    }
}
open class NgProtectedMethodBase {
    protected fun <R> pick(values: Array<R?>?): Array<R?>? = values
}
class NgProtectedMethodText : NgProtectedMethodBase() {
    private fun invoke(block: () -> Unit) = block()

    fun capturedSnapshot(values: Array<String?>): Array<String?> {
        var result: Array<String?>? = null
        invoke { result = pick<String>(values) }
        return result!!
    }
}
// An OVERRIDE narrowing a base `T?` slot to a concrete one — at the HEAD and NESTED in a constructed generic. The
// derived declaration holds `Int?`/`NgBox<Int?>`, which no `Nullable(Tv)` sweep can see, yet the CLR slot it must
// fill is the base's erased one; emitted narrowed it is a new overload and the type does not load.
//
// The two positions are closed DIFFERENTLY, and the test below drives each through BOTH of its entry points — the
// base slot and the override's own declared type — because that is what tells them apart. At the HEAD the override
// keeps `accept(Nullable<int32>)` and a private bridge fills the base's `accept(object)`, so both entry points name
// a real member. NESTED, no conversion exists in either direction (`NgBox<object>` and `NgBox<Nullable<int32>>` are
// unrelated invariant reified generics), so the declaration itself has to move onto the base's shape and the two
// entry points are one member.
class NgBox<T>(val v: T?)
interface NgSink<T> { fun accept(x: T?): String; fun boxed(b: NgBox<T?>): String }
class NgIntSink : NgSink<Int> {
    override fun accept(x: Int?): String = x?.toString() ?: "none"
    override fun boxed(b: NgBox<Int?>): String = b.v?.toString() ?: "none"
}
class NgTextSink : NgSink<String> {
    override fun accept(x: String?): String = x ?: "none"
    override fun boxed(b: NgBox<String?>): String = b.v ?: "none"
}
// The two shapes that put an ordinary `var` beside a value-position `try` inside ONE expression-position block —
// the layout a shape-keyed widening could not tell apart from the join's own temp. Both need the `if` branch block:
// that is what makes the enclosing block a value block rather than a statement list.
fun ngJoinNeighbourStatementTry(c: Boolean): Int {
    return if (c) {
        var x: Int = 5
        try { ngJoinNeighbourSide(); null } finally { ngJoinNeighbourSide() }
        x = x + 1
        x
    } else 0
}
fun ngJoinNeighbourSwallow(s: String, c: Boolean): Int {
    return if (c) {
        var n: Int = -1
        try { n = s.length } catch (e: Exception) { null }
        n
    } else 0
}
fun ngJoinNeighbourSide() {}
// A `T?` RETURN slot narrowed by an override, with a THIRD level below it. The return axis is where two bridge
// synthesizers can both see a divergence — the covariant-return one and this erasure's — and emitting both puts two
// private members with the same signature and the same MethodImpl descriptor on one class, of which the emitter
// binds whichever it matches first. Only the erasure's bridge forwards VIRTUALLY, so `NgSubSrc`'s override is
// reachable through the interface only when exactly one bridge owns the slot and it is that one.
interface NgSrc<T> { fun get(): T?; val v: T?; var w: T? }
open class NgBaseSrc : NgSrc<Int> {
    override fun get(): Int? = 4
    override val v: Int? = 40
    override var w: Int? = 400
}
class NgSubSrc : NgBaseSrc() {
    override fun get(): Int? = 9
    override val v: Int? get() = 90
}
// The same narrowing over a base CLASS. A class slot is wired by different CLR metadata than an interface slot, so
// it is a separate observable — an abstract slot left unfilled is a TypeLoadException at type LOAD, which no
// verification pass reports and only running the code can catch.
abstract class NgHolder<T> { abstract fun take(x: T?): String }
// The MIXED generic base slot: one parameter the erasure moves and one it does not. The bridge's descriptor states
// the CONSTRUCTED slot `(int32, object)` while the base method is emitted once, keyed by its DECLARATION `(T, object)`
// — so binding the MethodImpl has to go through the declaration and re-anchor onto the constructed base, exactly as
// the interface wiring does. Matching the constructed signature against the builder key instead refused this program.
abstract class NgMixed<T> { abstract fun take(x: T, narrowed: T?): String }
open class NgIntMixed : NgMixed<Int>() {
    override fun take(x: Int, narrowed: Int?): String = "" + x + "/" + narrowed
}
class NgIntMixedSub : NgIntMixed() {
    override fun take(x: Int, narrowed: Int?): String = "sub:" + super.take(x, narrowed)
}
open class NgIntHolder : NgHolder<Int>() { override fun take(x: Int?): String = x?.toString() ?: "none" }
// A THIRD level: the bridge in `NgIntHolder` forwards VIRTUALLY, so an override of the typed body one level down is
// what runs when the call comes through the erased base slot.
class NgIntHolderSub : NgIntHolder() { override fun take(x: Int?): String = "sub:" + super.take(x) }
// #86 §5.3 — the boundary of the overload-collision refusal, from the side that must KEEP COMPILING. `T?` and `Any?`
// both emit `object`, so a same-arity pair of them is one CLR signature and bir2cir refuses it
// (tests/compile-fail/NullableGenericOverloadCollision.kt). A pair differing in METHOD GENERIC ARITY is not: generic
// arity is part of the CLI signature (ECMA-335 I.8.6.1.6), so these are two slots, and each is reachable — Kotlin's
// own resolution picks the generic one for an explicit type argument and the non-generic one otherwise.
fun <T> ngArity(x: T?): String = "tv"
fun ngArity(x: Any?): String = "any"
class NgArityOwner {
    fun <T> pick(x: T?): String = "tv"
    fun pick(x: Any?): String = "any"
}
// A `vararg xs: T?` packs its arguments into an `Array<T?>` — erased to `object[]`. The PACK and its ELEMENTS are
// one operation: build the array at the erased element type and box each element into it. Built as
// `Nullable<int32>[]` it cannot be converted to the `object[]` the slot names afterwards, and the mismatched
// `newarr`/`stelem` pair corrupted memory (a segfault, not a diagnostic).
fun <T> ngVarargCount(vararg xs: T?): Int = xs.size
fun <T> ngVarargFirstNonNull(vararg xs: T?): T? {
    for (x in xs) if (x != null) return x
    return null
}

// #324 — a user generic taking a `List<A?>`. The value-element receiver conversion must fire on the RECEIVER's own
// nullable element and nowhere else; wrapping this ordinary parameter threw at run time.
fun <T> ngNullBoxes(x: T): List<T?> = listOf(x, null)
fun <A> ngCountPresent(l: List<A?>, extra: Int): Int {
    var n = 0
    for (e in l) if (e != null) n++
    return n + extra
}

// ---- #86 : carrier-argument erasure at every reified-argument position ----------------------------------------
// Each of these declares a CONCRETE nullable value type in one argument position, which is where the physical form
// moves: `List<Int?>` is an `IReadOnlyList<object>`, `Box<Int?>` a `Box<object>`, `(Int?) -> R` a
// `Func<object, R>`. Their `String?` twins are the controls — a reference `?` is not a physical difference on the
// CLR and keeps the element type, so a C# caller of the reference form still sees `string`.
fun ngSize(xs: List<Int?>): Int = xs.size
fun ngJoin(xs: List<Int?>): String {
    var s = ""
    for (x in xs) {
        if (s != "") s += ","
        s += x?.toString() ?: "null"
    }
    return s
}
fun ngSizeRef(xs: List<String?>): Int = xs.size
fun ngMutate(xs: MutableList<Int?>): Int {
    xs.add(7)                       // a write THROUGH the erased element slot: the value boxes into it
    xs.removeAt(1)                  // …and a structural mutation the caller must observe
    xs.add(null)
    var n = 0
    for (x in xs) if (x != null) n++
    return n
}
fun ngMapSize(m: Map<String, Int?>): Int = m.size
fun ngPairSecond(p: Pair<Int?, String>): String = p.second
class NgCarrier<T>(val v: T)
fun ngCarrierValue(c: NgCarrier<Int?>): Int? = c.v
fun ngNestedCount(xss: List<List<Int?>>): Int {
    var n = 0
    for (xs in xss) n += xs.size
    return n
}
// A delegate PARAMETER component, a delegate RETURN component, and the reference control for each.
fun ngApplyQ(x: Int?, f: (Int?) -> String): String = f(x)
fun ngApplyQRef(x: String?, f: (String?) -> String): String = f(x)
fun ngApplyToQ(x: Int, f: (Int) -> Int?): Int? = f(x)
// The targets of a CALLABLE REFERENCE into a `(Int?) -> String` slot. Their declared `Int?` parameter is the Kotlin
// surface an author wrote and a C# caller binds, so it must survive being referenced: erased to `object` it would be
// a public signature rewritten by a USE of it, with no carrier to restore it.
fun ngHandleQ(x: Int?): String = x?.toString() ?: "none"
class NgRefOwner { fun member(x: Int?): String = "m" + (x?.toString() ?: "none") }
// A `Iterable<T?>` slot at a value instantiation: the one slot an `Enumerable.Cast<object>` conversion inhabits, and
// the position where Kotlin's covariance over a value element has no CLR counterpart.
fun <T> ngCountIterable(xs: Iterable<T?>): Int {
    var n = 0
    for (x in xs) if (x != null) n++
    return n
}
// A generic METHOD whose instantiation is itself `Int?`: it must be emitted at `object` from the start, because
// `List<object>` is the only argument its `IReadOnlyList<!!0>` parameter accepts.
fun <T> ngFirstOr(xs: List<T>, d: T): T {
    for (x in xs) if (x != null) return x
    return d
}
// An OVERRIDE at `T = Int` whose parameter is a nullable-generic COLLECTION. The base's slot is
// `accept(IReadOnlyList<object>)` T-independently, so the override's own parameter is that same physical slot and
// nothing needs bridging; the reference instantiation beside it proves the shape is not value-specific.
abstract class NgListSink<T> { abstract fun accept(xs: List<T?>): String }
class NgIntListSink : NgListSink<Int>() {
    override fun accept(xs: List<Int?>): String {
        var n = 0
        for (x in xs) if (x != null) n++
        return n.toString()
    }
}
class NgTextListSink : NgListSink<String>() {
    override fun accept(xs: List<String?>): String {
        var n = 0
        for (x in xs) if (x != null) n++
        return n.toString()
    }
}

// ---- #86 : a generic supertype declared in ANOTHER assembly, implemented at `Int?` ---------------------------
// `kotlin.Comparable` lives in the stdlib, so the slot this class must fill is a REFERENCED declaration. Its
// argument erases (to `IComparable<object>`, collapsed to the non-generic `System.IComparable`) while the override's
// own parameter stays `Nullable<int32>`, so the two only meet through a bridge — and building that bridge means
// reading the supertype's declaration off the producing assembly.
class ngCmp(val v: Int) : Comparable<Int?> {
    override fun compareTo(other: Int?): Int = v - (other ?: 0)
}

// The SAME-MODULE control: the supertype is in this compilation, so the bridge reads it directly.
interface NgSlotSink<T> { fun accept(x: T): String }
class NgLocalSink : NgSlotSink<Int?> {
    override fun accept(x: Int?): String = x?.toString() ?: "none"
}

// ---- #345 : a constrained call closes its parameter slot over the receiver bound -----------------------------
// The bound itself is object-erased at `Int?`, so the constrained call dispatches through `NgBoundSink<object>`.
// Its value argument must therefore be boxed into that substituted physical slot. `Any?` is the reference control:
// it reaches the same object slot but needs no value-type widening.
interface NgBoundSink<T> { fun accept(x: T): String }
class NgBoundIntSink : NgBoundSink<Int?> {
    override fun accept(x: Int?): String = "i:" + (x?.toString() ?: "none")
}
class NgBoundAnySink : NgBoundSink<Any?> {
    override fun accept(x: Any?): String = "a:" + (x?.toString() ?: "none")
}
fun <T : NgBoundSink<Int?>> ngUseNullableValueBound(t: T): String = t.accept(1)
fun <T : NgBoundSink<Any?>> ngUseObjectBound(t: T): String = t.accept("x")

// ---- #287 : `is` against a NULLABLE type operand ACCEPTS null -------------------------------------------------
// `null is String?` / `null is Int?` are true in Kotlin, and the frontend RELIES on it: the else branch of
// `when { x is T? -> … }` carries a smart-cast to a NON-null `x`, which is what makes `x.toString()` there resolve
// to the `kotlin.Any` MEMBER rather than the null-safe `Any?.toString()` extension. `isinst` matches no null, so
// the `?` on the type operand must survive into the emit (bir2cir marks the node, ilemit adds the null branch).
fun <T> nullIsStringQ(t: T): Boolean = t is String?
fun <T> nullIsIntQ(t: T): Boolean = t is Int?
fun <T> nullIsStringNonNullQ(t: T): Boolean = t is String
// A SIDE-EFFECTING operand: the null answer is reached by branching on the value already on the stack, so the
// operand must still be evaluated exactly ONCE — on the null path as much as the non-null one.
var nullIsCalls = 0
fun nullIsSrc(n: Int): Any? { nullIsCalls++; return if (n > 0) "hi" else null }

// A CLR generic argument does not distinguish String from String?. Reified Kotlin declarations therefore receive a
// hidden Boolean witness, including when the type argument flows through another reified declaration.
inline fun <reified T> nullReifiedIs(x: Any?): Boolean = x is T
inline fun <reified U> nullReifiedForward(x: Any?): Boolean = nullReifiedIs<U>(x)
inline fun <reified A, reified B> nullReifiedEither(x: Any?): Boolean = x is A || x is B
inline fun <reified T> nullReifiedObject(x: Any?): Boolean = object {
    fun matches(): Boolean = x is T
}.matches()
fun interface NullReifiedChecker { fun matches(x: Any?): Boolean }
inline fun <reified T> nullReifiedSam(): NullReifiedChecker = NullReifiedChecker { it is T }
inline fun <reified T> nullReifiedSuspend(x: Any?): suspend () -> Boolean = { x is T }
inline fun <reified A, reified B> nullReifiedSecondClosure(x: Any?): Boolean = ({ x is B })()
inline fun <reified A, reified B> nullReifiedSecondObject(x: Any?): Boolean = object {
    fun matches(): Boolean = x is B
}.matches()
inline fun <reified A, reified B> nullReifiedSecondSam(): NullReifiedChecker =
    NullReifiedChecker { it is B }
inline fun <reified A, reified B> nullReifiedSecondSuspend(x: Any?): suspend () -> Boolean = { x is B }
class NullReifiedCarrier<T>(val value: Any?)
inline val <reified T> NullReifiedCarrier<T>.nullReifiedExtension: Boolean get() = value is T
inline fun <reified T> nullReifiedEmptyArray(): Array<T> = arrayOf<T>()
enum class NullReifiedEnum { A, B }
inline fun <reified T : Enum<T>> nullReifiedEnumValues(): Array<T> = enumValues<T>()

private fun nullRunImmediate(block: suspend () -> Boolean): Boolean {
    var outcome: Result<Boolean>? = null
    block.startCoroutine(object : Continuation<Boolean> {
        override val context: CoroutineContext get() = EmptyCoroutineContext
        override fun resumeWith(result: Result<Boolean>) { outcome = result }
    })
    return outcome!!.getOrThrow()
}

// ---- il-null : elvis / safe-call / not-null ------------------------------------------------------------------
fun nullUp(s: String?): String = s?.uppercase() ?: "none"
fun nullPick(a: String?, b: String): String = a ?: b

// ---- il-nullable-generic-list : a nullable generic element is object-erased at the declaration boundary -------
fun <T> nullBoxes(x: T): List<T?> = listOf(x, null)
fun <T> nullPlainBoxes(x: T): List<T> = listOf(x)

// ---- il-nullableprim : value-type nullable smart-cast / arithmetic / return must UNWRAP Nullable<T>.Value -----
fun nullAddOne(x: Int): Int = x + 1
fun nullFirstOr(n: Int?, d: Int): Int { if (n != null) return n; return d }
fun nullFirstOrExpr(n: Int?, d: Int): Int {
    val x: Int = if (n == null) d else return n // return in expression position must also unwrap Nullable<T>.Value
    return x
}

// ---- il-nullcs : #156 nullable String into a CharSequence?-receiver slot --------------------------------------
fun nullcsPick(n: Int): String? = if (n > 0) "hi" else null

// ---- il-safecallnv : A5 receiver-evaluated-once + nullable-value-type receiver unwrap -------------------------
var nvM = 0
fun nvG(): Char? { nvM++; return 'x' }
fun nvGn(): Char? { nvM++; return if (nvM < 0) 'x' else null }
fun nvS(): String? { nvM++; return "hey" }
fun nvSn(): String? { nvM++; return null }

// ---- il-safecallnvnullable : #198 safe-call over a VALUE-NULLABLE member (`b?.n` where `n: Int?`). `b.n` is already
// `Nullable<Int>`, so `b?.n` flattens to that same `Nullable<Int>` — kotc must NOT re-wrap it (a
// `newobj Nullable<int>(Nullable<int>)` -> InvalidProgram). Non-float twin of FloatTests' FltSafeNND repro. ---------
class NvIntBox(val n: Int?)
// arm-1 twin: a VALUE-NULLABLE RECEIVER (`Int?`) whose member (an extension returning `Int?`) is ALSO value-nullable
// -> `x?.nvHalfOrNull()` unwraps the receiver to `.Value` but the member result is already `Nullable<Int>` (must NOT
// re-wrap). Covers the recvElem != null branch of the #198 fix.
fun Int.nvHalfOrNull(): Int? = if (this % 2 == 0) this / 2 else null

// ---- il-trynullable : a nullable Int? return through try/finally; finally still runs --------------------------
val tryNullLog = mutableListOf<String>()
fun tryNullF(): Int? {
    try {
        return 1
    } finally {
        tryNullLog.add("fin")
    }
}

// ---- il-reqnn : requireNotNull / checkNotNull (reference + value-nullable) ------------------------------------
fun reqnnFirstChar(s: String?): Char = requireNotNull(s)[0]
fun reqnnMust(n: Int?): Int = checkNotNull(n)

class NullableTests {
    // #86 — the same-compilation `T?` declaration surface at T=Int / T=Boolean. The EMPTY / null lines are the real
    // measurement: a slot that cannot carry null reads back 0/false instead, which no non-null case would catch.
    @TestAttribute
    fun nullableGenericSurfaceAtValueTypes() {
        assertNull(ngFirstOrNull(listOf<Int>()))         // null  `T?` RETURN, empty at T=Int
        assertEquals(5, ngFirstOrNull(listOf(5, 6)))     // 5     same return, present
        assertNull(ngFirstOrNull(listOf<Boolean>()))     // null  `T?` RETURN, empty at T=Boolean
        val flag: Boolean? = ngFirstOrNull(listOf(true, false))
        assertTrue(flag == true)                         // true
        assertNull(ngFirstOrNull(listOf<String>()))      // null  reference control
        assertEquals(3, ngPick(3, 7))                    // 3     `T?` param carrying a value at T=Int
        assertEquals(7, ngPick<Int>(null, 7))            // 7     a NULL through the same param at T=Int
        assertTrue(ngPick<Boolean>(null, true))          // true  …and at T=Boolean
        assertEquals("x", ngPick<String>(null, "x"))     // x     …and at a REFERENCE type
        assertEquals(8, ngRoundTrip(8))                  // 8     param -> body-local -> return, all `T?`
        assertFalse(ngRoundTrip(false) ?: true)          // False  same chain at T=Boolean
        assertNull(ngRoundTrip<Int>(null))               // null   the chain carrying a null at T=Int
        assertNull(ngRoundTrip<String>(null))            // null   the chain carrying a null at a reference type
    }

    // #86 — the `T?` positions that are NOT a plain method slot: a constructor parameter with its backing field and
    // property, a constructor DELEGATION into an erased base slot, and an override narrowing a base `T?` to a
    // concrete one. Each is reached by its own walk and each failed differently before the erasure was uniform:
    // InvalidProgramException for the ctor slots, TypeLoadException for the override.
    @TestAttribute
    fun nullableGenericCtorAndOverrideSlots() {
        assertEquals(9, NgCell<Int>(null).orElse(9))     // 9     a null through a `T?` CTOR PARAM at T=Int
        assertEquals(4, NgCell(4).orElse(9))             // 4     the same slot carrying a value
        assertNull(NgCell<Int>(null).stored)             // null  the backing field/property read back
        assertEquals(2, NgCell(2).stored)                // 2
        assertEquals("s", NgCell<String>(null).orElse("s"))   // s   reference control
        assertNull(NgDerived(null).held)                 // null  ctor DELEGATION into an erased base slot at T=Int
        assertEquals(4, NgDerived(4).held)               // 4
        val strings = arrayOf<String?>("a", null)
        val snapshot = NgProtectedArrayText(strings).snapshot()
        assertTrue(snapshot === strings)                 // inherited protected `object[]` slot projects to String[]
        assertEquals("a", snapshot[0])
        assertNull(snapshot[1])
        val capturedSnapshot = NgProtectedArrayText(strings).capturedSnapshot()
        assertTrue(capturedSnapshot === strings)         // same boundary through a synthesized mutable-capture cell
        assertEquals("a", capturedSnapshot[0])
        assertNull(capturedSnapshot[1])
        val methodSnapshot = NgProtectedMethodText().capturedSnapshot(strings)
        assertTrue(methodSnapshot === strings)           // method-generic erasure on a non-generic protected owner
        assertEquals("a", methodSnapshot[0])
        assertNull(methodSnapshot[1])
        val si: NgSink<Int> = NgIntSink()
        assertEquals("none", si.accept(null))            // none  override narrowed to Int?, through the BASE slot
        assertEquals("3", si.accept(3))                  // 3
        assertEquals("none", si.boxed(NgBox(null)))      // none  the same, NESTED in a constructed generic
        assertEquals("6", si.boxed(NgBox(6)))            // 6
        // The OTHER entry point: the same override reached through its OWN declared type. It is a distinct member
        // from the erased base slot, and both must be callable — one of them silently missing is the shape a
        // consumer compiled against this assembly discovers, not one this assembly discovers about itself.
        assertEquals("none", NgIntSink().accept(null))   // none  own declared type, null
        assertEquals("7", NgIntSink().accept(7))         // 7     own declared type, a value
        assertEquals("8", NgIntSink().boxed(NgBox(8)))   // 8     the nested slot through its own type
        val ss: NgSink<String> = NgTextSink()
        assertEquals("none", ss.accept(null))            // none  reference control: dispatch must still find it
        assertEquals("none", ss.boxed(NgBox(null)))      // none
        assertEquals("x", NgTextSink().accept("x"))      // x     the reference control's own entry point
        assertEquals("y", NgTextSink().boxed(NgBox("y"))) // y    nested reference slot through its own declaration
    }

    // #86 D3 — the same narrowing over a base CLASS. Its slot is wired by different CLR metadata than an interface
    // slot's, and the failure it guards is a TypeLoadException at type LOAD: no verification pass reports it, so
    // only RUNNING the code catches it. Driven through the base slot, through the override's own declared type, and
    // one level further down, where the bridge's virtual forward is what makes the sub-override reachable.
    @TestAttribute
    fun nullableGenericBaseClassOverrideSlot() {
        val h: NgHolder<Int> = NgIntHolder()
        assertEquals("none", h.take(null))                    // none  the abstract base-CLASS slot, null
        assertEquals("5", h.take(5))                          // 5     the same slot, a value
        assertEquals("none", NgIntHolder().take(null))        // none  the override's own declared type
        assertEquals("6", NgIntHolder().take(6))              // 6
        val sub: NgHolder<Int> = NgIntHolderSub()
        assertEquals("sub:9", sub.take(9))                    // sub:9  through the base slot, dispatched one deeper
        assertEquals("sub:none", NgIntHolderSub().take(null)) // sub:none
        // A generic base whose slot mixes a moved and an unmoved parameter, through the base slot and one deeper.
        val m: NgMixed<Int> = NgIntMixed()
        assertEquals("1/null", m.take(1, null))               // 1/null  the constructed (int32, object) slot
        assertEquals("2/3", NgIntMixed().take(2, 3))          // 2/3     its own declared type
        val ms: NgMixed<Int> = NgIntMixedSub()
        assertEquals("sub:4/null", ms.take(4, null))          // sub:4/null
    }

    // #86 D3 — a `T?` RETURN slot, where TWO bridge synthesizers can see the divergence. Both emitting leaves the
    // class with two same-signature MethodImpls for one slot and the emitter picking between them; only the erasure
    // bridge forwards virtually, so a further-derived override is reachable through the base slot only if that one
    // owns it. Driven through the interface, through the concrete type, and one level down, for a method and for
    // read-only and mutable properties alike.
    @TestAttribute
    fun nullableGenericReturnSlotOverrideDispatch() {
        val s: NgSrc<Int> = NgSubSrc()
        assertEquals(9, s.get())                  // 9    the sub-override, through the erased interface slot
        assertEquals(90, s.v)                     // 90   the same for a read-only property
        s.w = 4000
        assertEquals(4000, s.w)                   // 4000 and for a mutable one, whose setter takes the erased slot
        assertEquals(9, NgSubSrc().get())         // 9    through the declared type
        assertEquals(90, NgSubSrc().v)
        val b: NgSrc<Int> = NgBaseSrc()
        assertEquals(4, b.get())                  // 4    the base's own body, so the sub-override is not just always winning
        assertEquals(40, b.v)
    }

    // #86 §3 — a value-position JOIN one branch leaves `null`, and the LOCAL that must not be dragged into it.
    // The widening is keyed on a frontend fact stamped on the join's own temp; keyed on the emitted shape instead,
    // an ordinary `var` standing next to a `try` inside an expression-position block was retyped to `Nullable<V>`
    // over an `int32` initializer — an AccessViolationException for the first shape below, and a silently wrong
    // answer for the second, which is the ordinary swallow-and-null idiom.
    @TestAttribute
    fun valuePositionJoinLeavesNeighbouringLocalsAlone() {
        assertEquals(6, ngJoinNeighbourStatementTry(true))    // 6  the local keeps its own int32 slot
        assertEquals(0, ngJoinNeighbourStatementTry(false))
        assertEquals(4, ngJoinNeighbourSwallow("abcd", true)) // 4  the try's own assignment survives
        assertEquals(0, ngJoinNeighbourSwallow("", true))     // 0  and an empty string is a length, not the sentinel
    }

    // #86 §5.3 — the arity boundary of the overload-collision refusal, pinned from the COMPILING side. Its refusing
    // twin is tests/compile-fail/NullableGenericOverloadCollision.kt; without this half the refusal could widen to
    // reject a legal pair and no gate would notice.
    @TestAttribute
    fun nullableGenericOverloadArityBoundary() {
        assertEquals("tv", ngArity<Int>(3))              // tv    the GENERIC slot, selected by an explicit type arg
        assertEquals("tv", ngArity<String>(null))        // tv    the same slot at a reference instantiation
        assertEquals("any", ngArity(3))                  // any   the NON-generic slot — a distinct CLR signature
        assertEquals("tv", NgArityOwner().pick<Int>(3))  // tv    the same pair as members of a type
        assertEquals("any", NgArityOwner().pick(3))      // any
    }

    // #86 — a `vararg xs: T?` argument list at a VALUE instantiation. The pack is an array CONSTRUCTION filling an
    // erased `object[]` slot, so it must be BUILT at the erased element type rather than converted afterwards.
    @TestAttribute
    fun nullableGenericVarargPack() {
        assertEquals(3, ngVarargCount<Int>(1, null, 3))          // 3     nulls and values in one pack at T=Int
        assertEquals(2, ngVarargCount<Boolean>(true, null))      // 2     …and at T=Boolean
        assertEquals(2, ngVarargCount<String>("a", null))        // 2     reference control
        // NOT driven here: an EMPTY vararg call. `f()` on a `vararg` emits no pack at all and the emitter refuses
        // ("CIR argument count mismatch … got 0, expected 1"), which has nothing to do with the erasure — a plain
        // `fun f(vararg xs: Int)` called as `f()` fails identically, with no generic and no `T?` in sight.
        assertEquals(5, ngVarargFirstNonNull<Int>(null, 5))      // 5     reading an element back out at T=Int
        assertNull(ngVarargFirstNonNull<Int>(null, null))        // null  every element absent
        assertEquals("b", ngVarargFirstNonNull<String>(null, "b"))
    }

    // #324 — the value-element collection conversion must key on the RECEIVER's own nullable element. Reading the
    // element off `typeArgs[0]` made it both miss `filterNotNullTo` (whose first type parameter is the destination)
    // and fire on an ordinary `List<A?>` parameter of a user generic, whose wrapped receiver then did not inhabit
    // the parameter slot at all.
    @TestAttribute
    fun nullableGenericCollectionArgKeysOnTheReceiver() {
        assertEquals(3, ngCountPresent(ngNullBoxes(7), 2))       // 3   one present element + 2, at T=Int
        assertEquals(2, ngCountPresent(ngNullBoxes("a"), 1))     // 2   reference control
        val vs: List<Int?> = listOf(1, null, 3, null, 5)
        val dest = mutableListOf<Int>()
        vs.filterNotNullTo(dest)
        assertEquals("1,3,5", dest.joinToString(","))            // 1,3,5  a two-type-parameter receiver conversion
        val bs: List<Boolean?> = listOf(true, null, false)
        assertEquals("True,False", bs.filterNotNull().joinToString(","))   // CLR-native Boolean stringification (§5)
    }

    // #86 — CARRIER-ARGUMENT ERASURE at every reified-argument position, driven at a VALUE instantiation where the
    // physical form actually moves. `X?` for a possibly-value `X` is `System.Object` inside a generic type argument,
    // a generic method's type argument, an array element and a delegate component alike, so `List<Int?>` is an
    // `IReadOnlyList<object>` and `Box<Int?>` is a `Box<object>`. Each of these drives a DIFFERENT emitted shape:
    // a member dispatch on the erased instantiation, a mutation through it, a construction that must be built at
    // `object` rather than converted afterwards, and a delegate whose lifted target has to declare the same slot.
    // The `String?` twins are the controls: a reference `?` is not a physical difference and must NOT move.
    @TestAttribute
    fun carrierArgumentErasureAcrossPositions() {
        // Read-only and mutable collections, constructed at the erased instantiation from the start.
        val ints: List<Int?> = listOf(1, null, 3)
        assertEquals(3, ngSize(ints))                             // 3
        assertEquals("1,null,3", ngJoin(ints))                    // the null element survives its erased slot
        val muts: MutableList<Int?> = mutableListOf(1, null)
        muts.add(4)
        muts.add(null)
        assertEquals(3, ngMutate(muts))                           // 3   mutation THROUGH the erased slot
        assertEquals("1,4,null,7,null", ngJoin(muts))             // the callee's writes and removal are the caller's
        val strs: List<String?> = listOf("a", null)               // reference control: still IReadOnlyList<string>
        assertEquals(2, ngSizeRef(strs))                          // 2

        // A map VALUE and a pair COMPONENT are ordinary type arguments.
        val m: Map<String, Int?> = mapOf("a" to 1, "b" to null)
        assertEquals(2, ngMapSize(m))                             // 2
        assertNull(m["b"])                                        // null survives the erased value slot
        assertEquals(1, m["a"])                                   // 1
        val p: Pair<Int?, String> = Pair(null, "x")
        assertEquals("x", ngPairSecond(p))                        // x
        assertNull(p.first)                                       // null

        // A USER generic class, constructed at `object` and read back through a value-typed consumer.
        assertEquals(7, ngCarrierValue(NgCarrier<Int?>(7)))       // 7
        assertNull(NgCarrier<Int?>(null).v)                       // null
        assertEquals("s", NgCarrier<String?>("s").v)              // s   reference control

        // NESTED arguments: `List<List<Int?>>` erases only the inner element.
        val nested: List<List<Int?>> = listOf(listOf(1, null), listOf(null))
        assertEquals(3, ngNestedCount(nested))                    // 3

        // DELEGATES. A `(Int) -> Int?` is a `Func<int32, object>`, so the lifted lambda's own RETURN must be
        // `object` too or there is no delegate; a `(Int?) -> String` keeps its `Func<Nullable<int32>, string>`
        // (§9c-bis's one recorded exception), so an argument handed to it takes the value-nullable wrap a direct
        // call has always had rather than pushing a bare `int32` into the slot.
        assertEquals("5", ngApplyQ(5) { it?.toString() ?: "none" })      // 5
        assertEquals("none", ngApplyQ(null) { it?.toString() ?: "none" })// none
        assertEquals(4, ngApplyToQ(2) { it * 2 })                        // 4   nullable RETURN component
        assertNull(ngApplyToQ(0) { null })                              // null
        assertEquals("s", ngApplyQRef("s") { it ?: "none" })            // s   reference control
        // A CALLABLE REFERENCE to a DECLARED member fills the same delegate slot, and its target's signature is the
        // author's, not a slot the erasure may move: `ngHandleQ` stays `handle(Nullable<int32>)` and the reference
        // still invokes correctly through a `(Int?) -> String` local. Both forms — the static `::fn` and the bound
        // `expr::member` — because they emit different delegate nodes.
        val viaRef: (Int?) -> String = ::ngHandleQ
        assertEquals("3", viaRef(3))                                     // 3
        assertEquals("none", viaRef(null))                               // none
        val viaBound: (Int?) -> String = NgRefOwner()::member
        assertEquals("m4", viaBound(4))                                  // m4
        assertEquals("mnone", viaBound(null))                            // mnone
        // KOTLIN COVARIANCE OVER A VALUE ELEMENT: `List<Int>` IS an `Iterable<Int?>`, while an
        // `IReadOnlyList<int32>` is not the `IEnumerable<object>` that slot erases to. The conversion is the
        // callee's to receive, and without it the iteration finds no `GetEnumerator` at all.
        assertEquals(3, ngCountIterable(listOf(1, 2, 3)))                // 3   non-nullable value element
        assertEquals(2, ngCountIterable(listOf<Int?>(1, null, 3)))       // 2   the nullable twin, already object
        assertEquals(2, ngCountIterable(listOf("a", "b")))               // 2   reference control

        // A generic METHOD instantiated at `Int?` must be instantiated at `object` from the start.
        assertEquals(2, ngFirstOr(listOf<Int?>(null, 2), 9) ?: 0)  // 2
        assertEquals(9, ngFirstOr(listOf<Int?>(null, null), 9))    // 9

        // An OVERRIDE whose parameter is a nullable-generic COLLECTION: the base slot is
        // `accept(IReadOnlyList<object>)` at every instantiation, and the override fills exactly that slot.
        assertEquals("2", NgIntListSink().accept(listOf(1, null, 3)))    // 2
        assertEquals("1", NgTextListSink().accept(listOf("a", null)))    // 1
    }

    // #86 — implementing a generic supertype at `Int?` when that supertype is declared in ANOTHER assembly.
    //
    // The supertype ARGUMENT is a reified argument and erases (`Comparable<Int?>` is an `IComparable<object>`, which
    // the lowering then collapses onto the non-generic `System.IComparable`), while the override's own parameter is a
    // DIRECT slot and correctly keeps its `Nullable<int32>`. Nothing then fills the base slot unless the bridge can
    // read the supertype's declaration — and `kotlin.Comparable` is declared in the stdlib, not here. Unfilled, the
    // type does not LOAD at all, which no verification pass reports and only running the code catches.
    //
    // The same-module control beside it is the shape that always worked: its supertype is in this compilation, so
    // the bridge reads the declaration directly. Both must hold, because they take different paths to one rule.
    @TestAttribute
    fun erasedSupertypeArgumentFromAnotherAssembly() {
        assertEquals(2, ngCmp(5).compareTo(3))              // 2    a REFERENCED generic supertype at Int?
        assertEquals(5, ngCmp(5).compareTo(null))           // 5    …and its null case
        val comparable: Comparable<Int?> = ngCmp(5)
        assertEquals(2, comparable.compareTo(3))
        assertEquals(5, comparable.compareTo(null))
        assertEquals("7", NgLocalSink().accept(7))          // 7    the same-module control
        assertEquals("none", NgLocalSink().accept(null))    // none
    }

    @TestAttribute
    fun constrainedCallUsesSubstitutedErasedParameterSlot() {
        assertEquals("i:1", ngUseNullableValueBound(NgBoundIntSink()))
        assertEquals("a:x", ngUseObjectBound(NgBoundAnySink()))
    }

    @TestAttribute
    fun nullableTypeOperandIsTest() {
        val n: Any? = nullcsPick(-1)                      // null, through a call so it is not const-folded
        val s: Any? = nullcsPick(1)                       // "hi"
        val i: Any? = 5
        assertTrue(n is String?)                         // true    null IS an instance of a nullable type
        assertTrue(n is Int?)                            // true    …of a nullable VALUE type too
        assertFalse(n !is String?)                       // false   the `!is` twin
        assertTrue(s is String?)                         // true    the non-null member still matches
        assertTrue(i is Int?)                            // true
        assertFalse(s is Int?)                           // false   a nullable operand widens null only, not the type
        assertFalse(i is String?)                        // false
        assertFalse(n is String)                         // false   a NON-nullable operand still rejects null
        assertFalse(n is Int)                            // false
        assertEquals("cs", when (n) { is String? -> "cs"; else -> "other" })  // cs  `when` subject branch
        assertTrue(nullIsStringQ<String?>(null))         // true    through a generic T (boxed `!!T` receiver)
        assertTrue(nullIsStringQ<String?>("a"))          // true
        assertFalse(nullIsStringQ<Int?>(3))              // false
        assertTrue(nullIsIntQ<Int?>(null))               // true
        assertTrue(nullIsIntQ<Int?>(3))                  // true
        assertFalse(nullIsStringNonNullQ<String?>(null)) // false   non-nullable operand through a generic T
        nullIsCalls = 0
        assertTrue(nullIsSrc(-1) is String?)             // true    null operand…
        assertEquals(1, nullIsCalls)                     // 1       …evaluated exactly once
        nullIsCalls = 0
        assertTrue(nullIsSrc(1) is String?)              // true    non-null operand…
        assertEquals(1, nullIsCalls)                     // 1       …evaluated exactly once
    }

    @TestAttribute
    fun nullableReifiedTypeArgumentIsTest() {
        val n: Any? = nullcsPick(-1)
        assertTrue(nullReifiedIs<String?>(n))
        assertTrue(nullReifiedIs<Int?>(n))
        assertTrue(nullReifiedIs<Any?>(n))
        assertTrue(nullReifiedForward<String?>(n))       // witness forwards through another reified parameter
        assertTrue(nullReifiedEither<String, Int?>(n))   // each reified parameter owns its witness slot
        assertFalse(nullReifiedEither<String, Int>(n))
        assertTrue(nullReifiedIs<String?>("a"))          // True   non-null member of a nullable instantiation
        assertFalse(nullReifiedIs<String?>(5))           // False  underlying type still differs
        assertTrue(nullReifiedIs<Any?>(5))
        assertTrue(nullReifiedIs<String>("a"))           // True
        assertFalse(nullReifiedIs<String>(n))            // False  correct: null is not a String
        assertTrue(nullReifiedIs<Int?>(5))               // True
        assertFalse(nullReifiedIs<Int>("a"))             // False
        assertTrue(nullReifiedObject<String?>(n), "lifted object")
        assertTrue(nullReifiedSam<Int?>().matches(n), "SAM shim")
        assertTrue(nullRunImmediate(nullReifiedSuspend<Any?>(n)), "suspend state machine")
        assertTrue(nullReifiedSecondClosure<String, Int?>(n), "dense closure witness map")
        assertTrue(nullReifiedSecondObject<String, Int?>(n), "dense object witness map")
        assertTrue(nullReifiedSecondSam<String, Int?>().matches(n), "dense SAM witness map")
        assertTrue(nullRunImmediate(nullReifiedSecondSuspend<String, Int?>(n)), "dense suspend witness map")
        assertTrue(NullReifiedCarrier<String?>(n).nullReifiedExtension, "generic property accessor")
        assertEquals(0, nullReifiedEmptyArray<String?>().size)      // semantic array factory sees visible arity
        assertEquals(2, nullReifiedEnumValues<NullReifiedEnum>().size) // enum intrinsic sees visible arity
        val source = arrayListOf<Any?>("a", null, "bb")
        assertEquals(listOf("a", null, "bb"), source.asSequence().filterIsInstance<String?>().toList())
    }

    @TestAttribute
    fun elvisSafeCallBang() {
        assertEquals("none", nullUp(null))               // none    s?.uppercase() ?: "none" when null
        assertEquals("HI", nullUp("hi"))                 // HI      safe-call chains through, uppercase
        assertEquals("fallback", nullPick(null, "fallback")) // fallback  a ?: b elvis fallback
        val s: String? = "abc"
        assertEquals("ABC", s!!.uppercase())             // ABC     `!!` yields the value, then member call
        assertEquals(5, "hello".length)                  // 5       String.length
    }

    @TestAttribute
    fun genericListErasure() {
        val strings = nullBoxes("a")
        assertEquals(2, strings.size)                    // 2       IReadOnlyCollection<object>.Count entry point
        assertEquals("a", strings[0])                    // a       get_Item on the erased interface
        val slog = mutableListOf<String>()
        for (value in strings) slog.add(value.toString()) // GetEnumerator over the erased interface
        assertEquals("a|null", slog.joinToString("|"))   // a / null
        val ints = nullBoxes(7)
        assertEquals(2, ints.size)                       // 2
        assertEquals(7, ints[0])                         // 7
        val ilog = mutableListOf<String>()
        for (value in ints) ilog.add(value.toString())
        assertEquals("7|null", ilog.joinToString("|"))   // 7 / null
        assertEquals(1, nullPlainBoxes("b").size)        // 1       plain generic element (no object erasure)
        assertEquals(2, listOf<String?>("c", null).size) // 2       concrete nullable element (no object erasure)
    }

    @TestAttribute
    fun valueTypeUnwrap() {
        val n: Int? = 7
        // n smart-cast to non-null Int inside the guard — every read/op must UNWRAP Nullable<T>.Value.
        if (n != null) {
            val z: Int = n
            assertEquals(7, z)                           // 7       assignment val z: Int = n
            assertEquals(107, z + 100)                   // 107     unwrapped arithmetic
            assertEquals(8, n + 1)                       // 8       arithmetic operand
            assertEquals(8, nullAddOne(n))               // 8       function arg
            assertEquals(14, n * 2)                      // 14
            assertEquals("gt5", if (n > 5) "gt5" else "le5") // gt5  comparison operand
        }
        assertEquals("big", if (n != null && n > 5) "big" else "small") // big  short-circuit && smart-cast
        assertEquals(7, nullFirstOr(7, -1))              // 7       return unwrapped value
        assertEquals(-1, nullFirstOr(null, -1))          // -1
        assertEquals(8, nullFirstOrExpr(8, -2))          // 8       return in expression position
        assertEquals(-2, nullFirstOrExpr(null, -2))      // -2
        val l: Long? = 100L
        if (l != null) {
            assertEquals(101L, l + 1L)                   // 101
            assertEquals(50L, l - 50L)                   // 50
            assertEquals("lgt", if (l > 99L) "lgt" else "lle") // lgt
        }
        val d: Double? = 2.5
        if (d != null) {
            val w: Double = d
            assertEquals(2.5, w)                         // 2.5
            assertEquals(2.75, w + 0.25)                 // 2.75
            assertEquals("dlt", if (d < 3.0) "dlt" else "dge") // dlt
        }
    }

    @TestAttribute
    fun notNullAssertion() {
        val n: Int? = 5
        assertEquals(5, n!!)                             // 5       value-type `!!` unwraps
        assertEquals(6, n!! + 1)                         // 6
        assertEquals(5L, n!!.toLong())                   // 5
        val z: Int? = null
        val npe = try { z!!; "no" } catch (e: NullPointerException) { "npe" }
        assertEquals("npe", npe)                          // npe     `!!` on null value-nullable throws NPE
        val l: Long? = 7L
        assertEquals(10L, l!! + 3L)                      // 10
        val d: Double? = 3.5
        assertEquals(3.75, d!! + 0.25)                   // 3.75
        val b: Byte? = 9
        assertEquals(9, b!!.toInt())                     // 9
        val u: UInt? = 5u
        assertEquals(6u, u!! + 1u)                       // 6       unsigned `!!` unwraps Nullable<uint>.Value
        val ub: UByte? = 9u
        assertEquals(9, ub!!.toInt())                    // 9
        val uz: UInt? = null
        val npeU = try { uz!!; "no" } catch (e: NullPointerException) { "npe-u" }
        assertEquals("npe-u", npeU)                       // npe-u
        val enumValue: NullReifiedEnum? = NullReifiedEnum.B
        val presentEnum: NullReifiedEnum = enumValue!!
        assertEquals(NullReifiedEnum.B, presentEnum)       // local enum Nullable<V> -> V uses the exact local type
    }

    @TestAttribute
    fun unsignedSafeCallElvisCast() {
        val us: UInt? = 5u
        assertEquals(5, us?.toInt())                     // 5       unsigned SAFE_CALL present -> unwrapped
        val un: UInt? = null
        assertNull(un?.toInt())                          // null    SAFE_CALL yields null when receiver is null
        assertEquals(6u, (us ?: 0u) + 1u)                // 6       ELVIS present -> unwrapped value
        assertEquals(9, (un ?: 9u).toInt())              // 9       ELVIS fallback
        val anyU: Any = 5u
        assertEquals(5, (anyU as? UInt)?.toInt())        // 5       unsigned `as?` value present -> unwrapped
        val anyS: Any = "x"
        assertNull(anyS as? UInt)                        // null    unsigned `as?` mismatch -> null
        val cU = true
        val juU: UInt? = if (cU) 5u else null
        assertEquals(5, juU?.toInt())                    // 5       if/else unsigned join present
    }

    @TestAttribute
    fun referenceEagerThrow() {
        val ok: String? = "hi"
        assertEquals("hi", ok!!)                         // hi      non-null reference `!!` yields the value
        assertEquals(2, ok!!.length)                     // 2       receiver-position `!!` still yields the value
        val s: String? = null
        val disc = try { s!!; "no" } catch (e: NullPointerException) { "npe-discard" }
        assertEquals("npe-discard", disc)                 // npe-discard  discarded `x!!` still throws EAGERLY
        val s2: String? = null
        val store = try { val y: String = s2!!; y } catch (e: NullPointerException) { "npe-store" }
        assertEquals("npe-store", store)                  // npe-store    stored `val y = x!!` still throws EAGERLY
    }

    @TestAttribute
    fun nullableInner() {
        // #100-H3: a nullable-inner collection type-arg (Map<String, List<Int>?>) upcast from a MutableMap must
        // still collapse its V and print Kotlin-style — the `?` must not smuggle an un-collapsed IReadOnlyList past
        // the Root-V collapse.
        val mm = mutableMapOf<String, MutableList<Int>>("a" to mutableListOf(1))
        val ro: Map<String, List<Int>?> = mm
        assertEquals("{a=[1]}", ro.toString())           // {a=[1]}
    }

    @TestAttribute
    fun stringIntoCharSequence() {
        val z: String? = null
        assertEquals("Z:empty", if (z.isNullOrEmpty()) "Z:empty" else "Z:$z")   // Z:empty  null short-circuits
        val v: String? = nullcsPick(1)
        assertEquals("V:hi", if (v.isNullOrEmpty()) "V:empty" else "V:$v")      // V:hi     adapter wrap, non-empty
        val e: String? = ""
        assertEquals("E:empty", if (e.isNullOrEmpty()) "E:empty" else "E:$e")   // E:empty  adapter, length 0
    }

    @TestAttribute
    fun requireCheckNotNull() {
        assertEquals('h', reqnnFirstChar("hello"))       // h       requireNotNull(s)[0]
        assertEquals(7, reqnnMust(7))                    // 7       checkNotNull(n) value-nullable
    }

    @TestAttribute
    fun safeCallNullableValue() {
        nvM = 0
        assertEquals(120, nvG()?.code)                   // 120     nullable VALUE receiver (Char?), unwrapped
        assertNull(nvGn()?.code)                          // null    null path
        assertEquals(3, nvS()?.length)                   // 3       reference receiver, value-type result
        assertNull(nvSn()?.length)                        // null    null path
        assertEquals(4, nvM)                             // 4       every receiver ran exactly once
    }

    @TestAttribute
    fun valueNullableMember() {
        // #198: `b?.n` over a value-nullable member (n: Int?) must not double-wrap the already-Nullable<Int> member.
        val present: NvIntBox? = NvIntBox(3)
        val innerNull: NvIntBox? = NvIntBox(null)
        val nullRecv: NvIntBox? = null
        assertEquals(3, present?.n)                       // 3       receiver present, member present
        assertNull(innerNull?.n)                          // null    receiver present, member null
        assertNull(nullRecv?.n)                           // null    receiver null -> flattened null
        // arm-1: value-nullable RECEIVER (Int?) + value-nullable member (extension -> Int?); receiver unwraps to
        // .Value, member stays Nullable<Int> (recvElem != null branch of the fix).
        val even: Int? = 8
        val odd: Int? = 5
        val nullR: Int? = null
        assertEquals(4, even?.nvHalfOrNull())             // 4       receiver present (even), member present
        assertNull(odd?.nvHalfOrNull())                   // null    receiver present (odd), member null
        assertNull(nullR?.nvHalfOrNull())                 // null    receiver null
    }

    @TestAttribute
    fun returnThroughFinally() {
        tryNullLog.clear()
        val r: Int? = tryNullF()
        assertEquals("fin", tryNullLog.joinToString("|")) // fin     finally ran (return inside try)
        assertEquals(1, r)                                // 1       nullable Int? return propagates
    }
}
