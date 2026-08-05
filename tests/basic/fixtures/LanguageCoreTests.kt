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
//                     -> rangeIn_bothBoundsAlwaysEvaluated / rangeIn_subjectReadAfterBounds — and in Kotlin's order
//                        (lo, hi, subject), which the short-circuit fast path used to skip and invert
//   il-whensubj       -> whenSubject_singleEval          A5 `when (subject)` in expr position evaluates subject exactly once
//   il-smartcast      -> smartCast_safeCast              `as?` safe cast — value type (-> T?) and reference type (-> isinst)
//   il-scope          -> scopeFunctions_inlined          let/run/with/also/apply -> inlined value-blocks (no delegate)
//
// Top-level names use feature stems (`ObjectFeature`, `CompanionExtension`, `InterfaceCompanion`,
// `OperatorOverload`, `UniversalMember`, `UserRange`, `rangeMembership`, `whenSubject`, and `smartCast`) so they
// remain readable and assembly-unique.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse

// ---- il-object : `object` singleton as shared mutable state; member access routes as instance access -------------
object ObjectFeatureCounter { var n = 0; fun inc() { n = n + 1 } }
interface ObjectFeatureState
object ObjectFeatureActive : ObjectFeatureState
object ObjectFeatureInactive : ObjectFeatureState
fun objectFeatureIsActive(value: Any?): Boolean = value is ObjectFeatureActive

// ---- il-objexpr : anonymous `object : Iface` implementing interface members -------------------------------------
interface ObjectFeatureGreeter { fun greet(): String }
fun objectFeatureMake(): ObjectFeatureGreeter = object : ObjectFeatureGreeter { override fun greet(): String = "hello from anon" }
interface ObjectFeatureOp { fun apply(x: Int): Int }
fun objectFeatureAdder(): ObjectFeatureOp = object : ObjectFeatureOp { override fun apply(x: Int): Int = x + 100 }

// ---- il-companionext : #177 extension fun in a companion -> static method whose first param is the ext receiver ---
class CompanionExtensionC {
    companion object {
        fun String.f(): Int = length + 1
        fun String.g(delta: Int): Int = length + delta
        fun Int.tripled(): Int = this * 3
    }
    // The companion extensions are only in scope inside a member of CompanionExtensionC; exercise the receiver-prepend across shapes.
    fun computeF(): Int = "abcd".f()
    fun computeG(): Int = "abcd".g(10)
    fun computeT(): Int = 7.tripled()
}

// ---- il-ifacecompanion : #83 an interface's PLAIN companion flattens to the interface's own statics --------------
interface InterfaceCompanionSharingStarted {
    fun tag(): Int
    companion object {
        val Eagerly: InterfaceCompanionSharingStarted = InterfaceCompanionStartedEagerly()
        val Lazily: InterfaceCompanionSharingStarted = InterfaceCompanionStartedLazily()
        const val VERSION: Int = 3
        fun describe(s: InterfaceCompanionSharingStarted): Int = s.tag() + VERSION
    }
}
class InterfaceCompanionStartedEagerly : InterfaceCompanionSharingStarted { override fun tag() = 1 }
class InterfaceCompanionStartedLazily : InterfaceCompanionSharingStarted { override fun tag() = 2 }
// Named companion (`Factory`): non-const `val` initialized by a call + a `const val`. Non-interface path non-regression.
class InterfaceCompanionChannel {
    companion object Factory {
        const val UNLIMITED: Int = 2147483647
        val CHANNEL_DEFAULT_CAPACITY: Int = interfaceCompanionComputeCap()
    }
}
fun interfaceCompanionComputeCap(): Int = 64

// ---- il-op : user-defined operator overloading ------------------------------------------------------------------
class OperatorOverloadVec(val x: Int, val y: Int) {
    operator fun plus(o: OperatorOverloadVec) = OperatorOverloadVec(x + o.x, y + o.y)
    operator fun minus(o: OperatorOverloadVec) = OperatorOverloadVec(x - o.x, y - o.y)
    operator fun times(k: Int) = OperatorOverloadVec(x * k, y * k)
    operator fun unaryMinus() = OperatorOverloadVec(-x, -y)
    operator fun get(i: Int): Int = if (i == 0) x else y
    operator fun compareTo(o: OperatorOverloadVec): Int = (x * x + y * y) - (o.x * o.x + o.y * o.y)
    operator fun contains(v: Int): Boolean = v == x || v == y
    operator fun invoke(): Int = x + y
    override fun toString(): String = "($x, $y)"
}
class OperatorOverloadBox(var v: Int) {
    operator fun get(i: Int): Int = v + i
    operator fun set(i: Int, value: Int) { v = value + i }
}

// ---- il-usermember : #96 declared vs inherited (kotlin.Any) hashCode/equals/toString dispatch -------------------
class UniversalMemberPoint(val x: Int, val y: Int) {
    override fun hashCode(): Int = x * 31 + y
    override fun equals(other: Any?): Boolean = other is UniversalMemberPoint && other.x == x && other.y == y
    override fun toString(): String = "($x, $y)"
}
class UniversalMemberPlain(val n: Int)                                        // no overrides -> inherits all three from kotlin.Any
open class UniversalMemberBase(val id: Int) { override fun toString(): String = "Base($id)" }
class UniversalMemberDerived(id: Int) : UniversalMemberBase(id)                            // reaches base toString; hashCode falls to Object slot
interface UniversalMemberNamed { fun label(): String }
class UniversalMemberWithName(val s: String) : UniversalMemberNamed {
    override fun label(): String = s
    override fun toString(): String = "WithName($s)"
}
class UniversalMemberNoName : UniversalMemberNamed { override fun label(): String = "x" }  // non-overriding: inherited Object.ToString -> type name

// ---- il-userrange : #73 `x in a..b` on a USER rangeTo/contains dispatches the real contains() -------------------
val userRangeLog = mutableListOf<String>()
class UserRangeVersion(val major: Int, val minor: Int) {
    operator fun rangeTo(other: UserRangeVersion) = UserRangeVersionRange(this, other)
    fun code(): Int = major * 100 + minor
}
class UserRangeVersionRange(val start: UserRangeVersion, val end: UserRangeVersion) {
    operator fun contains(v: UserRangeVersion): Boolean {
        userRangeLog.add("user contains")                              // was: println("user contains") — proves the real method runs
        return v.code() in start.code()..end.code()             // primitive range-membership fast path inside the user method
    }
}

// ---- il-rangein : #73 primitive range membership; side-effecting subject must be evaluated EXACTLY ONCE ----------
var rangeMembershipC = 0
fun rangeMembershipH(): Int { rangeMembershipC++; return 5 }
fun rangeMembershipHl(): Long { rangeMembershipC++; return 5L }
fun rangeMembershipHc(): Char { rangeMembershipC++; return 'e' }

// ...and range membership is `(lo..hi).contains(x)`, so EVALUATION ORDER is part of the meaning: the range is built
// first, which runs BOTH bounds unconditionally, left to right, and only then the subject. The short-circuit fast
// path (`x >= lo && x <op> hi`) would otherwise skip `hi` whenever the subject sits below `lo`, and would read a
// mutable subject before a bound had assigned it. Each function tags the log with the ROLE it plays.
val rangeMembershipLog = mutableListOf<String>()
fun rangeMembershipLo(): Int { rangeMembershipLog.add("lo"); return 1 }
fun rangeMembershipHi(): Int { rangeMembershipLog.add("hi"); return 10 }
fun rangeMembershipSubj(): Int { rangeMembershipLog.add("x"); return 5 }
fun rangeMembershipLoL(): Long { rangeMembershipLog.add("lo"); return 1L }
fun rangeMembershipHiL(): Long { rangeMembershipLog.add("hi"); return 10L }
fun rangeMembershipLoC(): Char { rangeMembershipLog.add("lo"); return 'a' }
fun rangeMembershipHiC(): Char { rangeMembershipLog.add("hi"); return 'z' }
private fun rangeMembershipTrace(): String = rangeMembershipLog.joinToString(",")

/**
 * One row of the evaluation-order matrix: clear the log, evaluate ONE membership expression, and assert in a
 * SINGLE comparison both what it answered and the order its operands ran in. [form] names the range form and
 * rides inside the compared strings, so a failure reads as `Expected: "CharRange -> False, evaluated lo,hi"` /
 * `But was: "CharRange -> False, evaluated lo"` — the row and the defect are both in the diff, with nothing to
 * deduce from an assertion's position. Only forms whose subject is a literal or a top-level call go through here:
 * a `var` subject must stay a PLAIN local, which passing it through a lambda would turn into a ref cell.
 */
private fun checkRangeForm(form: String, expect: Boolean, trace: String, membership: () -> Boolean) {
    rangeMembershipLog.clear()
    val answer = membership()
    assertEquals("$form -> $expect, evaluated $trace", "$form -> $answer, evaluated ${rangeMembershipTrace()}")
}

// ---- il-whensubj : A5 `when (subject)` in expression position evaluates its subject exactly ONCE ----------------
var whenSubjectN = 0
fun whenSubjectF(): Int { whenSubjectN++; return 2 }

// ---- il-smartcast : `as?` safe cast — value type (-> T?) and reference type (-> isinst) --------------------------
fun smartCastDescribe(x: Any): String { val n = x as? Int; return if (n != null) "int:$n" else "other" }
fun smartCastAsStr(x: Any): String { val s = x as? String; return s ?: "none" }

class LanguageCoreTests {
    @TestAttribute
    fun singleton() {
        ObjectFeatureCounter.n = 0
        ObjectFeatureCounter.inc(); ObjectFeatureCounter.inc(); ObjectFeatureCounter.inc()
        assertEquals(3, ObjectFeatureCounter.n)  // 3
        assertTrue(objectFeatureIsActive(ObjectFeatureActive))
        assertFalse(objectFeatureIsActive(ObjectFeatureInactive))
        assertFalse(objectFeatureIsActive("not an object singleton"))
    }

    @TestAttribute
    fun anonymous() {
        assertEquals("hello from anon", objectFeatureMake().greet())  // hello from anon
        assertEquals(105, objectFeatureAdder().apply(5))              // 105
    }

    @TestAttribute
    fun receiverPrepend() {
        val c = CompanionExtensionC()
        assertEquals(5, c.computeF())   // 5
        assertEquals(14, c.computeG())  // 14
        assertEquals(21, c.computeT())  // 21
    }

    @TestAttribute
    fun statics() {
        assertEquals(1, InterfaceCompanionSharingStarted.Eagerly.tag())                       // 1
        assertEquals(2, InterfaceCompanionSharingStarted.Lazily.tag())                        // 2
        assertEquals(3, InterfaceCompanionSharingStarted.VERSION)                             // 3
        assertEquals(4, InterfaceCompanionSharingStarted.describe(InterfaceCompanionSharingStarted.Eagerly))  // 4
        assertEquals(64, InterfaceCompanionChannel.CHANNEL_DEFAULT_CAPACITY)                  // 64
        assertEquals(2147483647, InterfaceCompanionChannel.UNLIMITED)                         // 2147483647
    }

    @TestAttribute
    fun userDefined() {
        val a = OperatorOverloadVec(3, 4)
        val b = OperatorOverloadVec(1, 2)
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
        val box = OperatorOverloadBox(0)
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
        val a = UniversalMemberPoint(1, 2)
        val b = UniversalMemberPoint(1, 2)
        val c = UniversalMemberPoint(3, 4)
        assertEquals(33, a.hashCode())           // 33   (declared: 1*31+2)
        assertTrue(a.equals(b))                  // True (declared structural)
        assertFalse(a.equals(c))                 // False
        assertTrue(a == b)                       // True (== routes through declared equals)
        assertEquals("(1, 2)", a.toString())     // (1, 2)
        assertEquals("(1, 2)", a.toString())     // (1, 2)  (was println(a) via println(Any?))
        val p = UniversalMemberPlain(7)
        val q = UniversalMemberPlain(7)
        assertTrue(p.hashCode() == p.hashCode()) // True  (stable inherited identity hash — no dead-end)
        assertFalse(p.equals(q))                 // False (inherited reference identity)
        assertTrue(p.equals(p))                  // True
        assertTrue(p == p)                       // True
        val d = UniversalMemberDerived(9)
        assertEquals("Base(9)", d.toString())    // Base(9)  (inherited declared toString)
        assertTrue(d.hashCode() == d.hashCode()) // True     (inherited Object.GetHashCode, stable)
        assertEquals("Base(9)", d.toString())    // Base(9)  (was println(d))
        val n1: UniversalMemberNamed = UniversalMemberWithName("hi")       // interface-typed receiver, overriding impl
        val n2: UniversalMemberNamed = UniversalMemberNoName()             // interface-typed receiver, non-overriding impl
        assertEquals("WithName(hi)", n1.toString())    // WithName(hi)
        assertTrue(n2.toString() == "UniversalMemberNoName")        // True (inherited Object.ToString -> runtime type name)
        assertTrue(n1.hashCode() == n1.hashCode())     // True
        assertTrue(n1.equals(n1))                      // True
        val hc: () -> Int = p::hashCode          // bound method reference to an inherited universal method
        assertTrue(hc() == p.hashCode())         // True (retargeted to System.Object::GetHashCode)
    }

    @TestAttribute
    fun userContains() {
        userRangeLog.clear()
        val lo = UserRangeVersion(1, 0)
        val hi = UserRangeVersion(2, 5)
        assertTrue(UserRangeVersion(1, 5) in lo..hi)   // user contains -> True
        assertFalse(UserRangeVersion(3, 0) in lo..hi)  // user contains -> False
        assertEquals("user contains|user contains", userRangeLog.joinToString("|"))  // real contains() ran exactly twice
    }

    @TestAttribute
    fun primitiveMembership() {
        rangeMembershipC = 0
        assertTrue(rangeMembershipH() in 1..10)       // True
        assertEquals(1, rangeMembershipC)             // 1 — not 2 (single evaluation)
        assertFalse(rangeMembershipH() in 1 until 5)  // False (5 excluded)
        assertEquals(2, rangeMembershipC)             // 2
        val i = 7                        // local subject over CONST bounds
        assertTrue(i in 1..10)           // True
        assertFalse(rangeMembershipH() in 1..<5)      // rangeUntil (..<): 5 in 1..4 -> False
        assertFalse(rangeMembershipH() !in 1..10)     // !in: 5 !in 1..10 -> False
        assertTrue(rangeMembershipHl() in 1L..10L)    // LongRange, side-effecting subject -> Long temp: True
        assertTrue(rangeMembershipHc() in 'a'..'z')   // CharRange, side-effecting subject -> Char temp: True
        assertEquals(6, rangeMembershipC)             // 6
        val r = 3..8                     // variable-held range: NOT the inline-construction fast path
        assertTrue(5 in r)               // real IntRange.contains binding: True
        // Every operand is re-readable, so nothing needs binding — the emitted shape is the bare comparison pair
        // (that part is the lowering's own contract; what a runtime assertion can pin is the value).
        assertTrue(5 in 1..10)           // True
        assertFalse(5 in 1..<5)          // False (5 excluded)
    }

    /** Both bounds build the range, so both run — even when the subject makes the comparison short-circuit. */
    @TestAttribute
    fun rangeIn_bothBoundsAlwaysEvaluated() {
        checkRangeForm("subject below lo", expect = false, trace = "lo,hi") { 0 in rangeMembershipLo()..rangeMembershipHi() }
        checkRangeForm("subject above hi", expect = false, trace = "lo,hi") { 99 in rangeMembershipLo()..rangeMembershipHi() }
        checkRangeForm("side-effecting subject", expect = true, trace = "lo,hi,x") { rangeMembershipSubj() in rangeMembershipLo()..rangeMembershipHi() }
        checkRangeForm("!in", expect = true, trace = "lo,hi") { 0 !in rangeMembershipLo()..rangeMembershipHi() }
        checkRangeForm("until extension", expect = false, trace = "lo,hi") { 0 in rangeMembershipLo() until rangeMembershipHi() }
        checkRangeForm("..< rangeUntil", expect = false, trace = "lo,hi") { 0 in rangeMembershipLo()..<rangeMembershipHi() }
        checkRangeForm("LongRange", expect = false, trace = "lo,hi") { 0L in rangeMembershipLoL()..rangeMembershipHiL() }
        checkRangeForm("CharRange", expect = false, trace = "lo,hi") { 'A' in rangeMembershipLoC()..rangeMembershipHiC() }
        // `downTo` builds an IntProgression, not a *Range, so the real contains() runs — same bound order.
        checkRangeForm("downTo IntProgression", expect = false, trace = "hi,lo") { 0 in rangeMembershipHi() downTo rangeMembershipLo() }
    }

    /** A mutable subject is read AFTER the bounds, so a bound that assigns it is visible to the comparison. */
    @TestAttribute
    fun rangeIn_subjectReadAfterBounds() {
        var x = -1
        assertTrue(x in run { x = 5; 0 }..50)   // lo assigns x -> 5 in 0..50
        assertEquals(5, x)
        var y = 100
        assertTrue(y in 0..run { y = 3; 10 })   // hi assigns y -> 3 in 0..10
        assertEquals(3, y)
        var z = 0
        assertTrue(z in run { z = 7; 5 }..9)    // 7 in 5..9 (reading z first would compare 0 >= 5)
        var w = 0
        assertTrue(w in run { w = 7; 5 } until 9)   // 7 in 5..8 (reading w first would compare 0 >= 5)
        // No lambda anywhere, so the subject stays a PLAIN local rather than a captured ref cell — the shape that a
        // "any local is safe to re-read" rule gets wrong on its own terms.
        var q = -1
        assertTrue(q in (if (q < 0) { q = 5; 0 } else 0)..50)
        assertEquals(5, q)
    }

    @TestAttribute
    fun singleEval() {
        whenSubjectN = 0
        val r = when (whenSubjectF()) {
            1 -> "a"
            2 -> "b"
            else -> "c"
        }
        assertEquals("b", r)   // b
        assertEquals(1, whenSubjectN)   // 1 — not once per branch test
        val s = when (whenSubjectF()) {  // else-hit: still exactly one evaluation
            0 -> "x"
            9 -> "y"
            else -> "z"
        }
        assertEquals("z", s)   // z
        assertEquals(2, whenSubjectN)   // 2
        val i = 7              // stable subject (immutable local): the direct-splice fast path
        val t = when (i) {
            7 -> "seven"
            else -> "other"
        }
        assertEquals("seven", t)  // seven
    }

    @TestAttribute
    fun safeCast() {
        assertEquals("int:42", smartCastDescribe(42))   // int:42
        assertEquals("other", smartCastDescribe("hi"))  // other
        assertEquals("yo", smartCastAsStr("yo"))        // yo
        assertEquals("none", smartCastAsStr(7))         // none
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
