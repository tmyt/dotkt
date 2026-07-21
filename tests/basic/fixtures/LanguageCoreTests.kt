// Language-core battery — objects, companions, operators, ranges, when/smart-cast, scope functions. Migrates the
// core-language family of cases/il-* onto the in-process NUnit suite. Each old case's `main` + stdout-golden diff
// becomes one @TestAttribute method whose per-value assertEquals/assertTrue/assertFalse is strictly stronger (typed)
// than the old text diff. Every value the old il_check asserted is preserved 1:1 (see the `// <expected>` comments).
// Ordered side-effecting `println`s (the "user contains" proof, `also`'s block) become captured log/counter state
// asserted directly — the STRUCTURE that was the actual subject (single-evaluation, real-method dispatch) is unchanged.
//
// Coverage preserved (old case -> method):
//   il-object         -> objectDecl_singleton            `object` singleton mutable state routed as instance access
//   il-objexpr        -> objectExpr_anonymous            anonymous `object : Iface` implementing interface members
//   il-companionext   -> companionExt_receiverPrepend    #177 extension fun in a companion -> static w/ leading receiver
//   il-ifacecompanion -> interfaceCompanion_statics      #83 interface PLAIN companion flattens to interface statics (.cctor)
//   il-op             -> operatorOverload_userDefined    user +/-/*/unaryMinus/get/set/compareTo/contains/invoke
//   il-ops            -> operators_bitwiseShiftLoop       do-while + and/or/xor/shl/shr/inv + numeric conversions
//   il-usermember     -> userMember_universalMethods     #96 declared vs inherited hashCode/equals/toString dispatch
//   il-userrange      -> userRange_userContains          #73 `x in a..b` on a USER rangeTo/contains dispatches the real method
//   il-rangein        -> rangeIn_primitiveMembership     #73 primitive range membership; subject evaluated exactly once
//   il-whensubj       -> whenSubject_singleEval          A5 `when (subject)` in expr position evaluates subject exactly once
//   il-smartcast      -> smartCast_safeCast              `as?` safe cast — value type (-> T?) and reference type (-> isinst)
//   il-scope          -> scopeFunctions_inlined          let/run/with/also/apply -> inlined value-blocks (no delegate)
//
// Top-level names are unique within this single battery assembly (one project = one namespace) and family-prefixed
// (`Obj`/`Ce`/`Ic`/`Op`/`Um`/`Ur`/`ri`/`ws`/`sc`) to avoid clashing with sibling batteries and stdlib.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse

// ---- il-object : `object` singleton as shared mutable state; member access routes as instance access -------------
object ObjCounter { var n = 0; fun inc() { n = n + 1 } }

// ---- il-objexpr : anonymous `object : Iface` implementing interface members -------------------------------------
interface ObjGreeter { fun greet(): String }
fun objMake(): ObjGreeter = object : ObjGreeter { override fun greet(): String = "hello from anon" }
interface ObjOp { fun apply(x: Int): Int }
fun objAdder(): ObjOp = object : ObjOp { override fun apply(x: Int): Int = x + 100 }

// ---- il-companionext : #177 extension fun in a companion -> static method whose first param is the ext receiver ---
class CeC {
    companion object {
        fun String.f(): Int = length + 1
        fun String.g(delta: Int): Int = length + delta
        fun Int.tripled(): Int = this * 3
    }
    // The companion extensions are only in scope inside a member of CeC; exercise the receiver-prepend across shapes.
    fun computeF(): Int = "abcd".f()
    fun computeG(): Int = "abcd".g(10)
    fun computeT(): Int = 7.tripled()
}

// ---- il-ifacecompanion : #83 an interface's PLAIN companion flattens to the interface's own statics --------------
interface IcSharingStarted {
    fun tag(): Int
    companion object {
        val Eagerly: IcSharingStarted = IcStartedEagerly()
        val Lazily: IcSharingStarted = IcStartedLazily()
        const val VERSION: Int = 3
        fun describe(s: IcSharingStarted): Int = s.tag() + VERSION
    }
}
class IcStartedEagerly : IcSharingStarted { override fun tag() = 1 }
class IcStartedLazily : IcSharingStarted { override fun tag() = 2 }
// Named companion (`Factory`): non-const `val` initialized by a call + a `const val`. Non-interface path non-regression.
class IcChannel {
    companion object Factory {
        const val UNLIMITED: Int = 2147483647
        val CHANNEL_DEFAULT_CAPACITY: Int = icComputeCap()
    }
}
fun icComputeCap(): Int = 64

// ---- il-op : user-defined operator overloading ------------------------------------------------------------------
class OpVec(val x: Int, val y: Int) {
    operator fun plus(o: OpVec) = OpVec(x + o.x, y + o.y)
    operator fun minus(o: OpVec) = OpVec(x - o.x, y - o.y)
    operator fun times(k: Int) = OpVec(x * k, y * k)
    operator fun unaryMinus() = OpVec(-x, -y)
    operator fun get(i: Int): Int = if (i == 0) x else y
    operator fun compareTo(o: OpVec): Int = (x * x + y * y) - (o.x * o.x + o.y * o.y)
    operator fun contains(v: Int): Boolean = v == x || v == y
    operator fun invoke(): Int = x + y
    override fun toString(): String = "($x, $y)"
}
class OpBox(var v: Int) {
    operator fun get(i: Int): Int = v + i
    operator fun set(i: Int, value: Int) { v = value + i }
}

// ---- il-usermember : #96 declared vs inherited (kotlin.Any) hashCode/equals/toString dispatch -------------------
class UmPoint(val x: Int, val y: Int) {
    override fun hashCode(): Int = x * 31 + y
    override fun equals(other: Any?): Boolean = other is UmPoint && other.x == x && other.y == y
    override fun toString(): String = "($x, $y)"
}
class UmPlain(val n: Int)                                        // no overrides -> inherits all three from kotlin.Any
open class UmBase(val id: Int) { override fun toString(): String = "Base($id)" }
class UmDerived(id: Int) : UmBase(id)                            // reaches base toString; hashCode falls to Object slot
interface UmNamed { fun label(): String }
class UmWithName(val s: String) : UmNamed {
    override fun label(): String = s
    override fun toString(): String = "WithName($s)"
}
class UmNoName : UmNamed { override fun label(): String = "x" }  // non-overriding: inherited Object.ToString -> type name

// ---- il-userrange : #73 `x in a..b` on a USER rangeTo/contains dispatches the real contains() -------------------
val urLog = mutableListOf<String>()
class UrVersion(val major: Int, val minor: Int) {
    operator fun rangeTo(other: UrVersion) = UrVersionRange(this, other)
    fun code(): Int = major * 100 + minor
}
class UrVersionRange(val start: UrVersion, val end: UrVersion) {
    operator fun contains(v: UrVersion): Boolean {
        urLog.add("user contains")                              // was: println("user contains") — proves the real method runs
        return v.code() in start.code()..end.code()             // primitive range-membership fast path inside the user method
    }
}

// ---- il-rangein : #73 primitive range membership; side-effecting subject must be evaluated EXACTLY ONCE ----------
var riC = 0
fun riH(): Int { riC++; return 5 }
fun riHl(): Long { riC++; return 5L }
fun riHc(): Char { riC++; return 'e' }

// ---- il-whensubj : A5 `when (subject)` in expression position evaluates its subject exactly ONCE ----------------
var wsN = 0
fun wsF(): Int { wsN++; return 2 }

// ---- il-smartcast : `as?` safe cast — value type (-> T?) and reference type (-> isinst) --------------------------
fun scDescribe(x: Any): String { val n = x as? Int; return if (n != null) "int:$n" else "other" }
fun scAsStr(x: Any): String { val s = x as? String; return s ?: "none" }

class LanguageCoreTests {
    @TestAttribute
    fun singleton() {
        ObjCounter.n = 0
        ObjCounter.inc(); ObjCounter.inc(); ObjCounter.inc()
        assertEquals(3, ObjCounter.n)  // 3
    }

    @TestAttribute
    fun anonymous() {
        assertEquals("hello from anon", objMake().greet())  // hello from anon
        assertEquals(105, objAdder().apply(5))              // 105
    }

    @TestAttribute
    fun receiverPrepend() {
        val c = CeC()
        assertEquals(5, c.computeF())   // 5
        assertEquals(14, c.computeG())  // 14
        assertEquals(21, c.computeT())  // 21
    }

    @TestAttribute
    fun statics() {
        assertEquals(1, IcSharingStarted.Eagerly.tag())                       // 1
        assertEquals(2, IcSharingStarted.Lazily.tag())                        // 2
        assertEquals(3, IcSharingStarted.VERSION)                             // 3
        assertEquals(4, IcSharingStarted.describe(IcSharingStarted.Eagerly))  // 4
        assertEquals(64, IcChannel.CHANNEL_DEFAULT_CAPACITY)                  // 64
        assertEquals(2147483647, IcChannel.UNLIMITED)                         // 2147483647
    }

    @TestAttribute
    fun userDefined() {
        val a = OpVec(3, 4)
        val b = OpVec(1, 2)
        assertEquals("(4, 6)", (a + b).toString())    // (4, 6)
        assertEquals("(2, 2)", (a - b).toString())    // (2, 2)
        assertEquals("(6, 8)", (a * 2).toString())    // (6, 8)
        assertEquals("(-3, -4)", (-a).toString())     // (-3, -4)
        assertEquals(3, a[0])                         // 3
        assertEquals(4, a[1])                         // 4
        assertTrue(a > b)                             // True
        assertTrue(b < a)                             // True
        assertFalse(2 in a)                           // False
        assertTrue(3 in a)                            // True
        assertEquals(7, a())                          // 7
        val box = OpBox(0)
        box[5] = 10
        assertEquals(15, box[0])                      // 15
    }

    @TestAttribute
    fun bitwiseShiftLoop() {
        var i = 0
        do { i = i + 1 } while (i < 3)
        assertEquals(3, i)             // 3
        assertEquals(2, 6 and 3)       // 2
        assertEquals(7, 6 or 1)        // 7
        assertEquals(3, 6 xor 5)       // 3
        assertEquals(16, 1 shl 4)      // 16
        assertEquals(15, 255 shr 4)    // 15
        assertEquals(-1, 0.inv())      // -1
        assertEquals(3, 3.7.toInt())   // 3
        assertEquals(5L, 5.toLong())   // 5
    }

    @TestAttribute
    fun universalMethods() {
        val a = UmPoint(1, 2)
        val b = UmPoint(1, 2)
        val c = UmPoint(3, 4)
        assertEquals(33, a.hashCode())           // 33   (declared: 1*31+2)
        assertTrue(a.equals(b))                  // True (declared structural)
        assertFalse(a.equals(c))                 // False
        assertTrue(a == b)                       // True (== routes through declared equals)
        assertEquals("(1, 2)", a.toString())     // (1, 2)
        assertEquals("(1, 2)", a.toString())     // (1, 2)  (was println(a) via println(Any?))
        val p = UmPlain(7)
        val q = UmPlain(7)
        assertTrue(p.hashCode() == p.hashCode()) // True  (stable inherited identity hash — no dead-end)
        assertFalse(p.equals(q))                 // False (inherited reference identity)
        assertTrue(p.equals(p))                  // True
        assertTrue(p == p)                       // True
        val d = UmDerived(9)
        assertEquals("Base(9)", d.toString())    // Base(9)  (inherited declared toString)
        assertTrue(d.hashCode() == d.hashCode()) // True     (inherited Object.GetHashCode, stable)
        assertEquals("Base(9)", d.toString())    // Base(9)  (was println(d))
        val n1: UmNamed = UmWithName("hi")       // interface-typed receiver, overriding impl
        val n2: UmNamed = UmNoName()             // interface-typed receiver, non-overriding impl
        assertEquals("WithName(hi)", n1.toString())    // WithName(hi)
        assertTrue(n2.toString() == "UmNoName")        // True (inherited Object.ToString -> runtime type name)
        assertTrue(n1.hashCode() == n1.hashCode())     // True
        assertTrue(n1.equals(n1))                      // True
        val hc: () -> Int = p::hashCode          // bound method reference to an inherited universal method
        assertTrue(hc() == p.hashCode())         // True (retargeted to System.Object::GetHashCode)
    }

    @TestAttribute
    fun userContains() {
        urLog.clear()
        val lo = UrVersion(1, 0)
        val hi = UrVersion(2, 5)
        assertTrue(UrVersion(1, 5) in lo..hi)   // user contains -> True
        assertFalse(UrVersion(3, 0) in lo..hi)  // user contains -> False
        assertEquals("user contains|user contains", urLog.joinToString("|"))  // real contains() ran exactly twice
    }

    @TestAttribute
    fun primitiveMembership() {
        riC = 0
        assertTrue(riH() in 1..10)       // True
        assertEquals(1, riC)             // 1 — not 2 (single evaluation)
        assertFalse(riH() in 1 until 5)  // False (5 excluded)
        assertEquals(2, riC)             // 2
        val i = 7                        // stable operand: the direct-splice fast path
        assertTrue(i in 1..10)           // True
        assertFalse(riH() in 1..<5)      // rangeUntil (..<): 5 in 1..4 -> False
        assertFalse(riH() !in 1..10)     // !in: 5 !in 1..10 -> False
        assertTrue(riHl() in 1L..10L)    // LongRange, side-effecting subject -> Long temp: True
        assertTrue(riHc() in 'a'..'z')   // CharRange, side-effecting subject -> Char temp: True
        assertEquals(6, riC)             // 6
        val r = 3..8                     // variable-held range: NOT the inline-construction fast path
        assertTrue(5 in r)               // real IntRange.contains binding: True
    }

    @TestAttribute
    fun singleEval() {
        wsN = 0
        val r = when (wsF()) {
            1 -> "a"
            2 -> "b"
            else -> "c"
        }
        assertEquals("b", r)   // b
        assertEquals(1, wsN)   // 1 — not once per branch test
        val s = when (wsF()) {  // else-hit: still exactly one evaluation
            0 -> "x"
            9 -> "y"
            else -> "z"
        }
        assertEquals("z", s)   // z
        assertEquals(2, wsN)   // 2
        val i = 7              // stable subject (immutable local): the direct-splice fast path
        val t = when (i) {
            7 -> "seven"
            else -> "other"
        }
        assertEquals("seven", t)  // seven
    }

    @TestAttribute
    fun safeCast() {
        assertEquals("int:42", scDescribe(42))   // int:42
        assertEquals("other", scDescribe("hi"))  // other
        assertEquals("yo", scAsStr("yo"))        // yo
        assertEquals("none", scAsStr(7))         // none
    }

    @TestAttribute
    fun inlined() {
        assertEquals(10, 5.let { it -> it * 2 })   // 10
        assertEquals(6, 5.run { this + 1 })        // 6
        assertEquals(9, with(3) { this * this })   // 9
        val log = mutableListOf<Int>()
        val alsoResult = 10.also { log.add(it) }   // also block runs (was: println(it) -> 10); returns receiver
        assertEquals(10, log[0])                   // 10 (the also block side effect)
        assertEquals(10, alsoResult)               // 10 (also returns receiver)
        assertEquals(7, 7.apply { })               // 7 (apply returns receiver)
    }
}
