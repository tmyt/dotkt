// Migrated IL fixture — core-language family. Each old case's `main` + stdout-golden diff becomes one
// @TestAttribute method whose per-value assertEquals/assertTrue is strictly stronger (typed) than the old text
// diff. Every value the old il_check asserted is preserved 1:1 (see the `// <expected>` comments). Ordered
// side-effecting `println`s (the destructuring-forEach) become captured list state asserted directly — the
// STRUCTURE that was the actual subject is unchanged.
//
// Coverage preserved (old case -> method):
//   il-iface      -> interfaceDispatch     interface method dispatch through a Greeter-typed param
//   il-infloopret -> infiniteLoopReturn    #141 value-returning while(true){…return x} (value-type Int + reference String)
//   il-inner      -> innerClassCapture     inner class captures the enclosing instance (base + private tag)
//   il-langfeat   -> languageFeatureSweep  anon fun, infix, tailrec, try/finally fall-through, abstract virtual dispatch, destructuring lambda param
//   il-langtail   -> longTailFeatures      `field` accessor, return-as-expression, lateinit read, when/&& smart-cast
//   il-localclass -> localClassLifting     function-local classes (captures, local data class, loop-declared local class)
//   il-loopjump   -> loopBreakContinue     E-0.5 break/continue/labeled-break inside CFG-lowered while loops
//
// All top-level declarations are DispatchCapture-prefixed (one project = one namespace, shared with sibling batteries + stdlib).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue

// ---- il-iface : interface method dispatch -----------------------------------------------------------------------
interface DispatchCaptureGreeter { fun greet(): String }
class DispatchCaptureEnglish : DispatchCaptureGreeter { override fun greet(): String = "Hello" }
class DispatchCaptureJapanese : DispatchCaptureGreeter { override fun greet(): String = "Konnichiwa" }
fun dispatchCaptureShout(g: DispatchCaptureGreeter): String = g.greet()

// ---- il-infloopret : #141 value-returning infinite loop (shared counter drives both returns) --------------------
private var dispatchCaptureInfN = 0
fun dispatchCaptureNextInt(): Int {
    while (true) {
        dispatchCaptureInfN++
        if (dispatchCaptureInfN >= 3) return dispatchCaptureInfN * 10
    }
}
fun dispatchCaptureFirstEven(): String {
    while (true) {
        dispatchCaptureInfN++
        if (dispatchCaptureInfN % 2 == 0) return "ok$dispatchCaptureInfN"
    }
}

// ---- il-inner : inner class captures the enclosing instance -----------------------------------------------------
class DispatchCaptureOuter(val base: Int) {
    private val tag = "T"
    inner class Counter(val step: Int) {
        var n = 0
        fun tick(): Int { n = n + 1; return base + n * step }   // base = outer's property
        fun label(): String = tag + n                            // tag = outer's private property
    }
    fun newCounter(step: Int): Counter = Counter(step)
}

// #555: an inherited inner constructor's hidden outer slot is declared as the immediate enclosing class, even when
// the value supplied for it is a transitively-derived `this`. Keep overload selection exact while projecting a
// generic derived receiver through the selected inner class's enclosing owner.
open class DispatchCaptureInnerBase<T>(private val outer: T) {
    inner class Entry {
        private val inner: String
        constructor(value: Int) { inner = "i$value" }
        constructor(value: String) { inner = "s$value" }
        fun render(): String = outer.toString() + ":" + inner
    }
    inner class GenericEntry<E>(private val value: E) {
        fun render(): String = outer.toString() + ":g" + value.toString()
    }
    inner class DefaultEntry(private val value: String = "default") {
        fun render(): String = outer.toString() + ":" + value
    }
}
open class DispatchCaptureInnerMiddle<T>(outer: T) : DispatchCaptureInnerBase<T>(outer)
class DispatchCaptureInnerLeaf<T>(outer: T) : DispatchCaptureInnerMiddle<T>(outer) {
    fun makeInt(value: Int): Entry = Entry(value)
    fun makeString(value: String): Entry = Entry(value)
    fun <E> makeGeneric(value: E): GenericEntry<E> = GenericEntry(value)
    fun makeDefault(): DefaultEntry = DefaultEntry()
}
class DispatchCaptureConcreteInnerLeaf : DispatchCaptureInnerBase<Int>(7) {
    fun make(value: Int): Entry = Entry(value)
}
class DispatchCaptureNestedOuter<E>(private val label: String) {
    override fun toString(): String = label
}
class DispatchCaptureNestedLeaf<E>(outer: DispatchCaptureNestedOuter<E>) :
    DispatchCaptureInnerBase<DispatchCaptureNestedOuter<E>>(outer) {
    fun make(value: Int): Entry = Entry(value)
}
class DispatchCaptureOwnerPair<A, B>
interface DispatchCaptureMarker
interface DispatchCaptureDependent<E>
class DispatchCaptureComparableDependent<E> :
    DispatchCaptureDependent<E>, Comparable<DispatchCaptureComparableDependent<E>> {
    override fun compareTo(other: DispatchCaptureComparableDependent<E>): Int = 0
}
fun <C : Comparable<C>> dispatchCaptureCompare(value: C): Int = value.compareTo(value)
open class DispatchCaptureConstrainedInnerOuter<T>(private val label: String) {
    inner class Token<E : T>(private val value: E?) {
        fun render(): String = label + ":" + (value?.toString() ?: "null")
    }
    inner class PairToken<E, F>(private val first: E?, private val second: F?) where E : T, F : T {
        fun render(): String = label + ":" + (first?.toString() ?: "null") + ":" + (second?.toString() ?: "null")
    }
    inner class MixedToken<E : T, F>(private val first: E?, private val second: F) {
        fun render(): String = label + ":" + (first?.toString() ?: "null") + ":" + second.toString()
    }
    inner class NestedToken<F, E>(private val value: E?) where E : DispatchCaptureOwnerPair<T, F> {
        fun render(): String = label + ":" + (value?.toString() ?: "null")
    }
    inner class NullableToken<E : T?>(private val value: E) {
        fun render(): String = label + ":" + (value?.toString() ?: "null")
    }
    inner class TransitiveToken<E, F>(private val value: E?) where E : T, F : List<E> {
        fun render(): String = label + ":" + (value?.toString() ?: "null")
    }
    inner class TransitiveValueToken<E, F>(private val values: F) where E : T, F : List<E> {
        fun size(): Int = values.size
    }
    inner class DirectMultiToken<E>(private val value: E?) where E : List<T>, E : DispatchCaptureMarker {
        fun render(): String = label + ":" + (value?.toString() ?: "null")
    }
    inner class TransitiveComparableToken<E, F>(private val value: F)
        where E : T, F : DispatchCaptureDependent<E>, F : Comparable<F> {
        fun compare(): Int = dispatchCaptureCompare(value)
    }
    inner class TransitiveValueTypeToken<E, F>(private val value: F) where E : T, F : Comparable<E> {
        fun get(): F = value
    }
}
class DispatchCaptureConstrainedInnerLeaf<T>(label: String) : DispatchCaptureConstrainedInnerOuter<T>(label)
fun dispatchCaptureConstrainedInner(value: DispatchCaptureConstrainedInnerOuter<*>): String =
    value.Token<Nothing>(null).render()
fun dispatchCaptureConstrainedPair(value: DispatchCaptureConstrainedInnerOuter<*>): String =
    value.PairToken<Nothing, Nothing>(null, null).render()
fun dispatchCaptureConstrainedMixed(value: DispatchCaptureConstrainedInnerOuter<*>): String =
    value.MixedToken<Nothing, String>(null, "mixed").render()
fun dispatchCaptureConstrainedNested(value: DispatchCaptureConstrainedInnerOuter<*>): String =
    value.NestedToken<String, Nothing>(null).render()
fun dispatchCaptureConstrainedNullable(value: DispatchCaptureConstrainedInnerOuter<*>): String =
    value.NullableToken(null).render()
fun dispatchCaptureConstrainedTransitive(value: DispatchCaptureConstrainedInnerOuter<*>): String =
    value.TransitiveToken<Nothing, List<Nothing>>(null).render()
fun dispatchCaptureConstrainedTransitiveValue(value: DispatchCaptureConstrainedInnerOuter<*>): Int =
    value.TransitiveValueToken<Nothing, List<Nothing>>(emptyList()).size()
fun dispatchCaptureConstrainedDirectMulti(value: DispatchCaptureConstrainedInnerOuter<*>): String =
    value.DirectMultiToken<Nothing>(null).render()
fun dispatchCaptureConstrainedTransitiveComparable(value: DispatchCaptureConstrainedInnerOuter<*>): Int =
    value.TransitiveComparableToken<Nothing, DispatchCaptureComparableDependent<Nothing>>(
        DispatchCaptureComparableDependent(),
    ).compare()
fun dispatchCaptureConstrainedTransitiveValueType(value: DispatchCaptureConstrainedInnerOuter<*>): Int =
    value.TransitiveValueTypeToken<Nothing, Int>(7).get()
fun dispatchCaptureConstrainedDerived(value: DispatchCaptureConstrainedInnerLeaf<*>): String {
    val kept = value.Token<Nothing>(null)
    return kept.render()
}
fun dispatchCaptureConstrainedFactory(value: DispatchCaptureConstrainedInnerOuter<*>) =
    value.Token<Nothing>(null)
fun dispatchCaptureConstrainedFactoryUse(value: DispatchCaptureConstrainedInnerOuter<*>): String =
    dispatchCaptureConstrainedFactory(value).render()
fun dispatchCaptureConstrainedLocalFactory(value: DispatchCaptureConstrainedInnerOuter<*>): String {
    fun construct() = value.Token<Nothing>(null)
    return construct().render()
}
fun dispatchCaptureConstrainedWideLocal(value: DispatchCaptureConstrainedInnerOuter<*>): String {
    var kept: Any = value.Token<Nothing>(null)
    kept = "wide"
    return if (kept == "wide") "wide" else "unexpected"
}
fun dispatchCaptureConstrainedMixedReturn(
    value: DispatchCaptureConstrainedInnerOuter<*>, returnToken: Boolean,
): Any = if (returnToken) value.Token<Nothing>(null) else "other"
fun dispatchCaptureMakeFromOutside(value: DispatchCaptureInnerLeaf<String>): String = value.Entry(9).render()
fun dispatchCaptureMakeNestedStarFromOutside(value: DispatchCaptureNestedLeaf<*>): String = value.Entry(12).render()
fun dispatchCaptureMakeNestedStarString(value: DispatchCaptureNestedLeaf<*>): String = value.Entry("value").render()
fun dispatchCaptureMakeNestedStarGeneric(value: DispatchCaptureNestedLeaf<*>): String = value.GenericEntry("generic").render()
fun dispatchCaptureMakeNestedStarDefault(value: DispatchCaptureNestedLeaf<*>): String = value.DefaultEntry().render()
fun dispatchCaptureKeepNestedStar(value: DispatchCaptureNestedLeaf<*>): String {
    val entry = value.Entry(13)
    return entry.render()
}
fun <X, D : DispatchCaptureNestedLeaf<*>> dispatchCaptureMakeThroughBound(ignored: X, value: D): String =
    value.Entry(14).render()

// ---- il-langfeat : anon fun / infix / tailrec / try-finally / abstract virtual dispatch -------------------------
val dispatchCaptureAdd = fun(a: Int, b: Int): Int = a + b
infix fun Int.dispatchCapturePow(e: Int): Int { var r = 1; var i = 0; while (i < e) { r *= this; i++ }; return r }
tailrec fun dispatchCaptureFact(n: Int, acc: Int): Int = if (n <= 1) acc else dispatchCaptureFact(n - 1, acc * n)
fun dispatchCaptureWithFinally(): String { var log = ""; try { log += "t" } finally { log += "f" }; return log }
abstract class DispatchCaptureShape(val name: String) {
    abstract fun area(): Int
    fun describe(): String = "$name=${area()}"
}
class DispatchCaptureSq(val s: Int) : DispatchCaptureShape("sq") { override fun area(): Int = s * s }
class DispatchCaptureCircle(val r: Int) : DispatchCaptureShape("circle") { override fun area(): Int = 3 * r * r }

// ---- il-langtail : `field` accessor / return-as-expression / lateinit read / smart-cast -------------------------
class DispatchCaptureLtCounter {
    var n: Int = 0
        get() = field
        set(v) { field = v + 1 }
}
class DispatchCaptureLtBox { lateinit var s: String }
fun dispatchCapturePick(x: Any): String = when (x) {
    is Int -> "int:" + (x + 1)
    is String -> "str:" + x.length
    else -> "other"
}
fun dispatchCaptureClassify(x: Any): String =
    if (x is Int && x > 10) "big:" + (x - 10) else "small"
fun dispatchCaptureFirstPositive(a: Int, b: Int): Int {
    val x = if (a > 0) a else return b
    return x * 100
}

class InterfaceAndLoopReturnTests {
    @TestAttribute
    fun interfaceDispatch() {
        assertEquals("Hello", dispatchCaptureShout(DispatchCaptureEnglish()))          // Hello
        assertEquals("Konnichiwa", dispatchCaptureShout(DispatchCaptureJapanese()))    // Konnichiwa
    }

    @TestAttribute
    fun infiniteLoopReturn() {
        dispatchCaptureInfN = 0
        assertEquals(30, dispatchCaptureNextInt())      // 30  (n reaches 3 -> 3*10)
        assertEquals("ok4", dispatchCaptureFirstEven()) // ok4 (continues from n=3 -> n=4, even)
    }

}

class NestedAndLocalClassTests {
    @TestAttribute
    fun innerClassCapture() {
        val o = DispatchCaptureOuter(100)
        val c = o.newCounter(10)
        assertEquals(110, c.tick())        // 110
        assertEquals(120, c.tick())        // 120
        assertEquals("T2", c.label())      // T2
        assertEquals(5, DispatchCaptureOuter(0).Counter(5).tick())  // 5 (inner built off an Outer receiver)
        val inherited = DispatchCaptureInnerLeaf("outer")
        assertEquals("outer:i42", inherited.makeInt(42).render())
        assertEquals("outer:svalue", inherited.makeString("value").render())
        assertEquals("outer:g11", inherited.makeGeneric(11).render())
        assertEquals("outer:default", inherited.makeDefault().render())
        assertEquals("7:i8", DispatchCaptureConcreteInnerLeaf().make(8).render())
        assertEquals("outer:i9", dispatchCaptureMakeFromOutside(inherited))
        assertEquals("nested:i10", DispatchCaptureNestedLeaf(DispatchCaptureNestedOuter<String>("nested")).make(10).render())
        val star = DispatchCaptureNestedLeaf(DispatchCaptureNestedOuter<String>("star"))
        assertEquals("star:i12", dispatchCaptureMakeNestedStarFromOutside(star))
        assertEquals("star:svalue", dispatchCaptureMakeNestedStarString(star))
        assertEquals("star:ggeneric", dispatchCaptureMakeNestedStarGeneric(star))
        assertEquals("star:default", dispatchCaptureMakeNestedStarDefault(star))
        assertEquals("star:i13", dispatchCaptureKeepNestedStar(star))
        assertEquals("star:i14", dispatchCaptureMakeThroughBound(Unit, star))
        val constrained: DispatchCaptureConstrainedInnerOuter<*> =
            DispatchCaptureConstrainedInnerOuter<String>("bound")
        assertEquals("bound:null", dispatchCaptureConstrainedInner(constrained))
        assertEquals("bound:null:null", dispatchCaptureConstrainedPair(constrained))
        assertEquals("bound:null:mixed", dispatchCaptureConstrainedMixed(constrained))
        assertEquals("bound:null", dispatchCaptureConstrainedNested(constrained))
        assertEquals("bound:null", dispatchCaptureConstrainedNullable(constrained))
        assertEquals("bound:null", dispatchCaptureConstrainedTransitive(constrained))
        assertEquals(0, dispatchCaptureConstrainedTransitiveValue(constrained))
        assertEquals(0, dispatchCaptureConstrainedTransitiveComparable(constrained))
        assertEquals("bound:null", dispatchCaptureConstrainedDirectMulti(constrained))
        assertEquals(7, dispatchCaptureConstrainedTransitiveValueType(constrained))
        assertEquals("bound:null", dispatchCaptureConstrainedFactoryUse(constrained))
        assertEquals("bound:null", dispatchCaptureConstrainedLocalFactory(constrained))
        assertEquals("wide", dispatchCaptureConstrainedWideLocal(constrained))
        assertEquals("other", dispatchCaptureConstrainedMixedReturn(constrained, false))
        val constrainedDerived: DispatchCaptureConstrainedInnerLeaf<*> =
            DispatchCaptureConstrainedInnerLeaf<Int>("derived-bound")
        assertEquals("derived-bound:null", dispatchCaptureConstrainedDerived(constrainedDerived))
    }

}

class LanguageFeatureTests {
    @TestAttribute
    fun languageFeatureSweep() {
        assertEquals(7, dispatchCaptureAdd(3, 4))                        // 7
        assertEquals(1024, 2 dispatchCapturePow 10)                      // 1024
        assertEquals(120, dispatchCaptureFact(5, 1))                     // 120
        assertEquals("tf", dispatchCaptureWithFinally())                 // tf
        val sh: DispatchCaptureShape = DispatchCaptureCircle(2)                       // base-typed -> virtual dispatch
        assertEquals("circle=12", sh.describe())            // circle=12
        assertEquals("sq=25", DispatchCaptureSq(5).describe())           // sq=25
        val log = mutableListOf<String>()
        listOf(1 to "a", 2 to "b").forEach { (n, s) -> log.add("$n$s") }  // destructuring lambda param
        assertEquals("1a,2b", log.joinToString(","))        // 1a, 2b
    }

    @TestAttribute
    fun longTailFeatures() {
        val c = DispatchCaptureLtCounter(); c.n = 5
        assertEquals(6, c.n)                       // 6 (setter +1, getter via field)
        val box = DispatchCaptureLtBox(); box.s = "hi"
        assertEquals("hi", box.s)                  // hi (lateinit)
        assertEquals("int:42", dispatchCapturePick(41))         // int:42
        assertEquals("str:3", dispatchCapturePick("abc"))       // str:3
        assertEquals("big:5", dispatchCaptureClassify(15))      // big:5
        assertEquals("small", dispatchCaptureClassify(3))       // small
        assertEquals(700, dispatchCaptureFirstPositive(7, 9))   // 700
        assertEquals(9, dispatchCaptureFirstPositive(-1, 9))    // 9 (return as expr)
    }

}

class LocalClassTests {
    @TestAttribute
    fun localClassLifting() {
        class L(val n: Int) { fun d() = n * 2 }
        assertEquals(10, L(5).d())                 // 10
        assertEquals(42, L(21).d())                // 42

        val k = 100
        class Cap { fun g() = k + 1 }              // captures outer local `k`
        assertEquals(101, Cap().g())               // 101

        data class P(val x: Int, val y: Int)       // local data class
        val p = P(3, 4)
        assertEquals("3,4", "${p.x},${p.y}")       // 3,4
        assertTrue(p == P(3, 4))                   // True

        var total = 0
        for (i in 1..3) { class Row(val v: Int); total += Row(i * 10).v }  // local class in a loop
        assertEquals(60, total)                    // 60
    }

}

class LoopControlTests {
    @TestAttribute
    fun loopBreakContinue() {
        var i = 0
        while (true) { if (i == 3) break; i = i + 1 }
        assertEquals("break at 3", "break at $i")  // 3

        var j = 0; var sumOdd = 0
        while (j < 6) { j = j + 1; if (j % 2 == 0) continue; sumOdd = sumOdd + j }
        assertEquals("sumOdd=9", "sumOdd=$sumOdd")  // 1+3+5 = 9

        var a = 0; var hit = "none"
        outer@ while (a < 3) {
            var b = 0
            while (b < 3) {
                if (a + b == 3) { hit = "$a,$b"; break@outer }
                b = b + 1
            }
            a = a + 1
        }
        assertEquals("outer break at 1,2", "outer break at $hit")  // 1,2
    }
}
