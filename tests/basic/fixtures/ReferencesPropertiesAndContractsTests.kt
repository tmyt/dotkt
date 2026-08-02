// Migrated fixture — pure-language core battery. Migrates the pure-Kotlin, same-module family of cases/il-* onto the
// in-process NUnit suite: nested classes, callable/property references, contracts, operator overloads, tuples,
// preconditions, custom accessors, ref-cell capture, reified type params, non-local return through inline repeat, and
// property delegation. Each old case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value
// assertEquals/assertTrue/assertFalse is strictly stronger (typed) than the old text diff. Every value the old
// il_check asserted is preserved 1:1 (see the `// <expected>` comments). Ordered side-effecting `println`s (the "fin"
// finally-order proof, the delegate get/set trace) are captured as log-list state and asserted directly — the STRUCTURE
// that was the actual subject is unchanged.
//
// Coverage preserved (old case -> method):
//   il-mrefpriv         -> mrefpriv_privateBoundRefInClosure  #155 bound ref to a PRIVATE method captured in a lifted closure
//   il-nested           -> nested_flattenedTypes              nested (non-inner) user classes -> flattened top-level types
//   il-nestlam          -> nestlam_lambdaList                 list of captured-i lambdas, each invoked
//   il-nncontract       -> nncontract_nonNullContracts        #6/#32 param preconditions + return postconditions -> NPE fail-fast
//   il-overload         -> overload_byParamSignature          overloaded funs resolve by name + parameter signature
//   il-overrideprop     -> overrideprop_slotFill              `override val` accessor fills the base/interface abstract slot
//   il-pair             -> pair_tupleDestructure              Pair (a to b) -> ValueTuple, .first/.second, destructuring
//   il-pairnest         -> pairnest_nestedCollectionToString  nested collection/map inside Pair/Triple.toString
//   il-precond          -> precond_requireCheckErrorTodo      #73 require/check/error/TODO + top-level repeat inline loop
//   il-propref          -> propref_callableReferences         #70 ::prop -> real KProperty0/KMutableProperty1
//   il-props            -> props_customAccessorsLateinit       get()/set() with `field` + lateinit semantics
//   il-refcell          -> refcell_capturedVarPromotion       captured-and-mutated outer var -> shared heap ref-cell
//   il-refcell-nullable -> refcell_nullableValueTypeCell      #36 captured var of a value-type nullable -> Nullable<T> ref-cell
//   il-reified          -> reified_typeParams                 reified T via targeted inline expansion (simpleName/is/as)
//   il-repeatnlr        -> repeatnlr_nonLocalReturn           #75 non-local return + return@repeat + nested repeat
//   il-rwp              -> rwp_readWriteDelegate               `by` ReadWriteProperty delegation trace
//
// COLLISION: this is one assembly / one namespace, so EVERY top-level declaration introduced here is prefixed `ReferenceProperty`
// with a per-case tag (Mref/Nst/Nnc/Ov/Op/Pref/Props/Rc/Rf/Rpt/Rwp) to avoid clashing with sibling batteries + stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse
import kotlin.reflect.KProperty0
import kotlin.reflect.KMutableProperty1
import kotlin.properties.ReadWriteProperty
import kotlin.reflect.KProperty

// ---- il-mrefpriv : #155 bound ref to a PRIVATE method captured in a lifted closure over `this` -------------------
class ReferencePropertyMrefBox {
    private fun secret(): String = "secret"
    fun deferred(): () -> String {
        val make: () -> (() -> String) = { this::secret }   // this::secret lives in a lifted closure over `this`
        return make()
    }
}

// ---- il-nested : nested (non-inner) user classes, flattened to top-level synthetic types --------------------------
class ReferencePropertyNstOuter(val tag: String) {
    class Node(val v: Int) {
        fun describe(): String = "node($v)"
        class Leaf(val w: Int) { fun show(): String = "leaf $w" }
    }
    fun label(): String = "outer:$tag"
}

// ---- il-nncontract : #6 non-null CONTRACTS on the public surface (param preconditions + return postconditions) ----
val referencePropertyNncLog = mutableListOf<String>()

@Suppress("UNCHECKED_CAST")
fun <T> referencePropertyNncForceNull(): T = null as T   // launder a null into a non-null reference slot (unchecked cast)

fun referencePropertyNncGreet(s: String): Int = s.length            // public top-level fun: param precondition

class ReferencePropertyNncBox(val name: String) {                    // public ctor: param precondition
    fun tag(x: String): String = x + name             // public member fun: param precondition
    val leakyProp: String get() = referencePropertyNncForceNull()    // public getter: return postcondition
    fun leakM(): String = referencePropertyNncForceNull()            // public member fun: return postcondition
}

fun referencePropertyNncLeak(): String = referencePropertyNncForceNull()            // public top-level fun: return postcondition

fun referencePropertyNncLeakExpr(): String {                         // expression-position return needs the same postcondition
    val unreachable: String = if (false) "ok" else return referencePropertyNncForceNull()
    return unreachable
}

fun referencePropertyNncLeakInTry(): String {                        // return POSTCONDITION wrap evaluated INSIDE a try region
    try { return referencePropertyNncForceNull() } finally { referencePropertyNncLog.add("fin") }  // NPE thrown in-try -> finally runs, then propagates
}

// ---- il-overload : overloaded user functions resolve by name + parameter signature -------------------------------
fun referencePropertyOvRender(s: String): String = "S:" + s
fun referencePropertyOvRender(f: () -> String): String = "F:" + f()
fun referencePropertyOvRender(n: Int): String = "I:" + n
class ReferencePropertyOvBox {
    fun put(s: String): String = "bs:" + s
    fun put(f: () -> String): String = "bf:" + f()
}
// #86 Phase 0: CLR method identity includes METHOD generic arity. These have the same name and physical `object`
// parameter vector but are distinct declarations (`arity=0` vs `arity=1`); ilemit must neither `$dup`-mangle the
// generic sibling nor route its body/call through the non-generic MethodBuilder.
fun referencePropertyOvByMethodArity(x: Any?): String = "plain"
fun <T> referencePropertyOvByMethodArity(x: Any?): String = "generic"

// ---- il-overrideprop : `override val` accessor fills the base/interface abstract slot ----------------------------
interface ReferencePropertyOpHasCtx { val ctx: Int }
abstract class ReferencePropertyOpBase(override val ctx: Int) : ReferencePropertyOpHasCtx { abstract fun run(): Int }
class ReferencePropertyOpImpl(ctx: Int) : ReferencePropertyOpBase(ctx) { override fun run(): Int = ctx * 2 }
abstract class ReferencePropertyOpAbstractHolder { abstract val value: Int }
class ReferencePropertyOpHolder(override val value: Int) : ReferencePropertyOpAbstractHolder()

// ---- il-propref : #70 ::prop callable references lower to a real KProperty0/KMutableProperty1 --------------------
var referencePropertyPrefX: Int = 1
class ReferencePropertyPrefObj(var p: Int)
fun referencePropertyPrefReadK(kp: KProperty0<Int>): Int = kp.get()
class ReferencePropertyPrefBox<T>(val value: T)
fun <T> referencePropertyPrefRefOf(b: ReferencePropertyPrefBox<T>): KProperty0<T> = b::value   // generic context: vType is a `tv`
class ReferencePropertyPrefPayload(val tag: String)
class ReferencePropertyPrefHolder(var pay: ReferencePropertyPrefPayload)                       // vType is an app-declared TypeBuilder class

// ---- il-props : custom property accessors (get()/set() with `field`) + lateinit semantics -----------------------
class ReferencePropertyPropsBox(v: Int) {
    var x: Int = v
        get() = field * 2
        set(value) { field = value + 1 }
    val doubled: Int get() = x + x          // computed property (no backing field)
}
class ReferencePropertyPropsSvc { lateinit var name: String }

// ---- il-refcell-nullable : #36 captured var of a value-type nullable -> Nullable<T> heap ref-cell ---------------
inline fun referencePropertyRcRun2(b: () -> Unit) { b() }

// ---- il-reified : reified type params via targeted inline expansion ---------------------------------------------
inline fun <reified T> referencePropertyRfTypeName(): String = T::class.simpleName ?: "?"
inline fun <reified T> referencePropertyRfIsA(x: Any): Boolean = x is T
inline fun <reified T> referencePropertyRfAsT(x: Any): String = (x as? T)?.toString() ?: "no"

// ---- il-repeatnlr : #75 NON-LOCAL return + return@repeat through inline repeat ----------------------------------
fun referencePropertyRptFirstIndexHitting(target: Int): Int {
    repeat(10) { i ->
        if (i == target) return i        // NON-LOCAL return from referencePropertyRptFirstIndexHitting
    }
    return -1
}
fun referencePropertyRptSumSkippingOdd(n: Int): Int {
    var s = 0
    repeat(n) { i ->
        if (i % 2 == 1) return@repeat    // labeled return = continue to next iteration
        s = s + i
    }
    return s
}

// ---- il-rwp : `by` ReadWriteProperty delegation trace (println side-effects captured into a log) ---------------
val referencePropertyRwpLog = mutableListOf<String>()
class ReferencePropertyRwpTrace(var v: Int) : ReadWriteProperty<Any?, Int> {
    override fun getValue(thisRef: Any?, property: KProperty<*>): Int {
        referencePropertyRwpLog.add("get " + property.name)
        return v
    }
    override fun setValue(thisRef: Any?, property: KProperty<*>, value: Int) {
        referencePropertyRwpLog.add("set " + property.name + " = " + value)
        v = value
    }
}
class ReferencePropertyRwpBox { var n: Int by ReferencePropertyRwpTrace(0) }

class PrivateCallableReferenceTests {
    // il-mrefpriv (#155): a bound ref to a PRIVATE method captured in a lifted closure over `this`.
    @TestAttribute
    fun privateBoundRefInClosure() {
        val b = ReferencePropertyMrefBox()
        assertEquals("secret", b.deferred()())   // secret
    }

    // il-nested: nested (non-inner) user classes flattened to top-level synthetic types.
}

class NestedTypeAndLambdaTests {
    @TestAttribute
    fun flattenedTypes() {
        assertEquals("outer:root", ReferencePropertyNstOuter("root").label())   // outer:root
        val n = ReferencePropertyNstOuter.Node(7)
        assertEquals("node(7)", n.describe())                    // node(7)
        assertEquals(14, n.v * 2)                                // 14
        assertEquals("leaf 3", ReferencePropertyNstOuter.Node.Leaf(3).show())   // leaf 3
    }

    // il-nestlam: list of captured-i lambdas, each invoked.
    @TestAttribute
    fun lambdaList() {
        val fs = (1..3).map { i -> { i } }
        assertEquals("[1, 2, 3]", fs.map { it() }.toString())   // [1, 2, 3]
    }

    // il-nncontract (#6/#32): param preconditions + return postconditions throw NPE fail-fast on a laundered null.
}

class NullContractTests {
    @TestAttribute
    fun nonNullContracts() {
        referencePropertyNncLog.clear()
        // normal non-null calls are unaffected
        assertEquals(2, referencePropertyNncGreet("hi"))            // 2
        assertEquals("tb", ReferencePropertyNncBox("b").tag("t"))   // tb

        // PRECONDITIONS: a null across the boundary -> fail-fast NullPointerException at each entry
        val nullStr: String = referencePropertyNncForceNull()
        var npeParam = false; try { referencePropertyNncGreet(nullStr) } catch (e: NullPointerException) { npeParam = true }
        assertTrue(npeParam)                          // npe-param
        var npeCtor = false; try { ReferencePropertyNncBox(nullStr) } catch (e: NullPointerException) { npeCtor = true }
        assertTrue(npeCtor)                           // npe-ctor
        var npeMember = false; try { ReferencePropertyNncBox("b").tag(nullStr) } catch (e: NullPointerException) { npeMember = true }
        assertTrue(npeMember)                         // npe-member

        // POSTCONDITIONS: a null leaking OUT of a non-null return -> NullPointerException at the return
        var npeRet = false; try { referencePropertyNncLeak() } catch (e: NullPointerException) { npeRet = true }
        assertTrue(npeRet)                            // npe-ret
        var npeRetExpr = false; try { referencePropertyNncLeakExpr() } catch (e: NullPointerException) { npeRetExpr = true }
        assertTrue(npeRetExpr)                        // npe-retexpr
        var npeRetM = false; try { ReferencePropertyNncBox("b").leakM() } catch (e: NullPointerException) { npeRetM = true }
        assertTrue(npeRetM)                           // npe-retm
        var npeGetter = false; try { ReferencePropertyNncBox("b").leakyProp } catch (e: NullPointerException) { npeGetter = true }
        assertTrue(npeGetter)                         // npe-getter
        var npeTrRet = false; try { referencePropertyNncLeakInTry() } catch (e: NullPointerException) { npeTrRet = true }
        assertTrue(npeTrRet)                          // npe-trret
        // finally ran FIRST ("fin"), then the postcondition NPE propagated
        assertEquals("fin", referencePropertyNncLog.joinToString("|"))  // fin
    }

    // il-overload: overloaded funs resolve by name + parameter signature (not name alone).
}

class OverloadPropertyAndTupleTests {
    @TestAttribute
    fun byParamSignature() {
        assertEquals("S:x", referencePropertyOvRender("x"))     // S:x
        assertEquals("F:y", referencePropertyOvRender { "y" })  // F:y
        assertEquals("I:7", referencePropertyOvRender(7))       // I:7
        val b = ReferencePropertyOvBox()
        assertEquals("bs:p", b.put("p"))         // bs:p
        assertEquals("bf:q", b.put { "q" })      // bf:q
        assertEquals("plain", referencePropertyOvByMethodArity(null))
        assertEquals("generic", referencePropertyOvByMethodArity<Int>(null))
    }

    // il-overrideprop: `override val` accessor fills the base/interface abstract slot (not a fresh NewSlot).
    @TestAttribute
    fun slotFill() {
        val h: ReferencePropertyOpHasCtx = ReferencePropertyOpImpl(21)
        assertEquals(21, h.ctx)                  // 21
        assertEquals(42, (h as ReferencePropertyOpBase).run())  // 42
        val a: ReferencePropertyOpAbstractHolder = ReferencePropertyOpHolder(7)
        assertEquals(7, a.value)                 // 7
    }

    // il-pair: Pair (a to b) -> ValueTuple, .first/.second, destructuring.
    @TestAttribute
    fun tupleDestructure() {
        val p = 3 to 4
        assertEquals(3, p.first)     // 3
        assertEquals(4, p.second)    // 4
        val q = "x" to 10
        assertEquals("x", q.first)   // x
        assertEquals(10, q.second)   // 10
        val (a, b) = 5 to 6
        assertEquals(11, a + b)      // 11
    }

    // il-pairnest: nested collection/map inside Pair/Triple.toString routes each component through the collection-aware stringifier.
    @TestAttribute
    fun nestedCollectionToString() {
        assertEquals("([1, 2], [3, 4])", (listOf(1, 2) to listOf(3, 4)).toString())          // ([1, 2], [3, 4])
        assertEquals("([1], [2], [3])", Triple(listOf(1), listOf(2), listOf(3)).toString())  // ([1], [2], [3])
        assertEquals("({1=2}, [3])", (mapOf(1 to 2) to listOf(3)).toString())                // ({1=2}, [3])
        assertEquals("(1, (2, 3))", (1 to (2 to 3)).toString())                              // (1, (2, 3))
        assertEquals("([[1]], 5)", (listOf(listOf(1)) to 5).toString())                      // ([[1]], 5)
        assertEquals("(1, 2)", (1 to 2).toString())                                          // (1, 2)   (scalars unaffected)
        assertEquals("(null, a)", (null to "a").toString())                                  // (null, a)
    }

    // il-precond (#73 M6/M7): require/check/error/TODO throw-synthesis + top-level repeat inline counter loop.
    @TestAttribute
    fun requireCheckErrorTodo() {
        val acc = IntArray(1)
        repeat(3) { i -> acc[0] = acc[0] + i }
        assertEquals(3, acc[0])            // 3  (0 + 1 + 2, index 0..n-1, captured acc)
        require(acc[0] == 3)               // passes (no throw)
        check(acc[0] == 3)                 // passes (no throw)
        var req = false; try { require(false) } catch (e: IllegalArgumentException) { req = true }
        assertTrue(req)                    // req
        var chk = false; try { check(false) } catch (e: IllegalStateException) { chk = true }
        assertTrue(chk)                    // chk
        var errMsg = ""; try { error("boom") } catch (e: IllegalStateException) { errMsg = "err:${e.message}" }
        assertEquals("err:boom", errMsg)   // err:boom
        var todo = false; try { TODO() } catch (e: NotImplementedError) { todo = true }
        assertTrue(todo)                   // todo
    }

    // il-propref (#70): ::prop callable references lower to real KProperty0/KMutableProperty1 implementations.
}

class PropertyReferenceAndAccessorTests {
    @TestAttribute
    fun callableReferences() {
        referencePropertyPrefX = 1
        assertEquals("referencePropertyPrefX", ::referencePropertyPrefX.name)   // property-ref .name = the declared name (was `x`; renamed for assembly-wide collision freedom)
        assertEquals(1, ::referencePropertyPrefX.get())    // 1
        referencePropertyPrefX = 2
        ::referencePropertyPrefX.set(99)
        assertEquals(99, referencePropertyPrefX)           // 99
        assertEquals(99, (::referencePropertyPrefX)())     // 99

        val obj = ReferencePropertyPrefObj(7)
        assertEquals(7, obj::p.get())               // 7
        assertEquals(7, ReferencePropertyPrefObj::p.get(obj))      // 7
        assertEquals(99, referencePropertyPrefReadK(::referencePropertyPrefX))    // 99

        assertEquals("g", referencePropertyPrefRefOf(ReferencePropertyPrefBox("g")).get())   // g  — generic-lift `tv` vType
        val hp: KMutableProperty1<ReferencePropertyPrefHolder, ReferencePropertyPrefPayload> = ReferencePropertyPrefHolder::pay
        val h = ReferencePropertyPrefHolder(ReferencePropertyPrefPayload("t1"))
        hp.set(h, ReferencePropertyPrefPayload("t2"))
        assertEquals("t2", hp.get(h).tag)   // t2 — app-class vType, unbound mutable ref
        assertEquals("pay", hp.name)        // pay
    }

    // il-props: custom get()/set() accessors with the `field` backing identifier + lateinit semantics.
    @TestAttribute
    fun customAccessorsLateinit() {
        val b = ReferencePropertyPropsBox(10)
        assertEquals(20, b.x)          // field 10, get *2 = 20
        b.x = 3                        // set: field = 3+1 = 4
        assertEquals(8, b.x)           // get: 4*2 = 8
        assertEquals(16, b.doubled)    // x + x = 16

        val s = ReferencePropertyPropsSvc()
        var notInit = false
        try { s.name } catch (e: Exception) { notInit = true }   // lateinit access throws
        assertTrue(notInit)            // not initialized
        s.name = "ready"
        assertEquals("ready", s.name)  // ready
    }

    // il-refcell: a captured-and-mutated outer var is promoted to a shared heap cell so the write is visible.
}

class CapturedVariableCellTests {
    @TestAttribute
    fun capturedVarPromotion() {
        var counter = 0
        val inc = { counter++ }                       // non-inline lambda mutates a captured var
        inc(); inc(); inc()
        assertEquals(3, counter)                      // 3

        var total = 0
        val adder = object { fun add(n: Int) { total += n } }   // object expression mutates a captured var
        adder.add(10); adder.add(20)
        assertEquals(30, total)                       // 30

        var log = ""
        class Logger { fun put(s: String) { log += s } }        // local class mutates a captured var
        val l = Logger()
        l.put("a"); l.put("b")
        assertEquals("ab", log)                       // ab

        var sum = 0
        listOf(1, 2, 3, 4).forEach { sum += it }      // (inline) forEach over the same cell
        assertEquals(10, sum)                         // 10
    }

    // il-refcell-nullable (#36): a captured-and-mutated `var Int?`/`Long?`/`Double?` -> Nullable<T> heap ref-cell.
    @TestAttribute
    fun nullableValueTypeCell() {
        // INLINE closure: captured-and-mutated `var Int?` with a smart-cast READ (q -> bare Int) AND a WRITE.
        var q: Int? = 5
        referencePropertyRcRun2 {
            if (q != null) {
                val x: Int = q            // smart-cast READ into a bare-Int slot -> Nullable<int>.Value
                assertEquals(5, x)        // 5
                assertEquals(6, q + 1)    // direct smart-cast READ in an operator -> 6
                q = x + 100               // WRITE bare Int -> Nullable<int> slot
            }
        }
        assertEquals(105, q)              // 105

        // NON-INLINE closure ref-cells of other value-nullable widths, plus a `null` write.
        var l: Long? = 5L
        var d: Double? = 1.5
        val step = {
            l = (l ?: 0L) + 10L
            d = (d ?: 0.0) + 0.5
        }
        step()
        step()
        assertEquals(25L, l)              // 25
        assertEquals(2.5, d)              // 2.5
        l = null
        assertTrue(l == null)             // null
    }

    // il-reified: reified type params via targeted inline expansion (simpleName / is / as?).
}

class ReifiedAndNonLocalReturnTests {
    @TestAttribute
    fun typeParams() {
        assertEquals("String", referencePropertyRfTypeName<String>())  // String
        assertEquals("Int32", referencePropertyRfTypeName<Int>())      // Int32  (CLR simpleName of Int)
        assertTrue(referencePropertyRfIsA<String>("hi"))               // True
        assertFalse(referencePropertyRfIsA<Int>("hi"))                 // False
        assertTrue(referencePropertyRfIsA<Int>(42))                    // True
        assertEquals("yo", referencePropertyRfAsT<String>("yo"))       // yo
        assertEquals("no", referencePropertyRfAsT<String>(7))          // no
    }

    // il-repeatnlr (#75): NON-LOCAL return + return@repeat + nested repeat + scope-fn-in-repeat through inline repeat.
    @TestAttribute
    fun nonLocalReturn() {
        assertEquals(3, referencePropertyRptFirstIndexHitting(3))    // 3   (non-local return out of the loop)
        assertEquals(-1, referencePropertyRptFirstIndexHitting(99))  // -1  (loop completes, falls through)
        assertEquals(6, referencePropertyRptSumSkippingOdd(6))       // 6   (0 + 2 + 4, odd indices skipped via return@repeat)
        var acc = 0
        repeat(4) { acc += it }                       // capture + implicit `it`
        assertEquals(6, acc)                          // 6   (0 + 1 + 2 + 3)

        // nested repeat: nested callInline hygiene (distinct loop vars, inner index resolves independently)
        var grid = 0
        repeat(3) { i -> repeat(2) { j -> grid = grid + i * 10 + j } }
        assertEquals(63, grid)                        // 63  (sum of i*10+j over i=0..2, j=0..1)

        // a scope function inside a repeat body must NOT destroy the outer index (`it`) binding
        var m2 = 0
        repeat(3) { val a = it.let { it + 1 }; m2 = m2 + a + it }
        assertEquals(9, m2)                           // 9   ((0+1)+0 + (1+1)+1 + (2+1)+2)
    }

    // il-rwp: `by` ReadWriteProperty delegation — the get/set trace is captured into a log and asserted in order.
}

class ReadWritePropertyDelegateTests {
    @TestAttribute
    fun readWriteDelegate() {
        referencePropertyRwpLog.clear()
        val b = ReferencePropertyRwpBox()
        b.n = 5
        assertEquals(5, b.n)   // the read returns 5
        // ordered delegate side-effects: setValue("set n = 5") then getValue("get n")
        assertEquals("set n = 5|get n", referencePropertyRwpLog.joinToString("|"))  // set n = 5 ; get n
    }
}
