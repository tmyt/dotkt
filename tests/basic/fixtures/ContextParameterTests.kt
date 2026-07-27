// Kotlin CONTEXT-PARAMETER battery. A context parameter needs no opt-in at language version 2.4, so every shape
// below is reachable from ordinary user source.
//
// THE RULE UNDER TEST: a context parameter is projected as an ordinary POSITIONAL value parameter — after the
// `__self` extension receiver, before the regular parameters, in `IrFunction.parameters` order — and the
// declaration's parameter list, the call's argument list, the overload signature key and the `@KotlinDefault`
// index all count that one physical sequence. Every case here is a shape that failed before that rule was applied
// on BOTH sides of a call: the call sites dropped the context argument (short arg list -> InvalidProgramException,
// or a silent null for a generic context type), the overload/`paramSig` key was one arity short of the
// declaration it selected, and an omitted default reading a context parameter emitted a dangling local.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

class CtxScale(val factor: Int)
class CtxTag(val label: String)
class CtxBox<T>(val v: T)

// --- functions ------------------------------------------------------------------------------------------------
context(s: CtxScale)
fun ctxScaled(a: Int): Int = a * s.factor

context(s: CtxScale, t: CtxTag)
fun ctxTwoContexts(a: Int): String = t.label + (a * s.factor)

context(s: CtxScale)
fun String.ctxDeco(a: Int): String = this + ":" + (a * s.factor)

// The context argument is threaded through a NESTED call whose own context comes from the caller's parameter.
context(s: CtxScale)
fun ctxOuter(a: Int): Int = ctxScaled(a) + 1

// A generic context parameter: the dropped argument used to arrive as `null` (a silent wrong answer, not a
// verifier error), so this case is the one that proves the argument is really passed, not merely counted.
context(b: CtxBox<T>)
fun <T> ctxUnwrap(): T = b.v

// `contextOf<T>()` is itself an ordinary stdlib context function, so it only resolves once the rule holds.
context(_: CtxScale)
fun ctxViaContextOf(a: Int): Int = a * contextOf<CtxScale>().factor

// --- defaults that read a context parameter -------------------------------------------------------------------
// The omitted default is evaluated in the callee's scope with the context parameter bound to THIS call's context
// argument — the same by-symbol substitution an earlier value parameter or a receiver gets.
context(s: CtxScale)
fun ctxDefaultOnly(b: Int = s.factor): Int = b

context(s: CtxScale)
fun ctxDefaultReadsBoth(a: Int, b: Int = a + s.factor): Int = b

context(s: CtxScale)
fun String.ctxDefaultReadsAll(a: Int, b: Int = a + s.factor + this.length): Int = b

// Two contexts, a receiver, and defaults reading each of them — plus a named-middle omission, which is what pins
// the @KotlinDefault/`defaultArgParam` index space to the physical sequence (a context slot shifts every later index).
context(s: CtxScale, t: CtxTag)
fun String.ctxMixed(a: Int = 1, b: Int = a + s.factor, d: String = t.label + this): String = "$a/$b/$d"

// --- overloads: the `sig` key must count the context slots, or ilemit resolves by name alone ------------------
context(s: CtxScale)
fun ctxOv(vararg xs: Int): Int = xs.sum() + s.factor

context(s: CtxScale)
fun ctxOv(x: String): String = x + s.factor

// --- inline: `pc` (the payload key's param count) must count the context slots too ----------------------------
context(s: CtxScale)
inline fun ctxInline(f: (Int) -> Int): Int = f(s.factor)

// --- properties -----------------------------------------------------------------------------------------------
var ctxStore: Int = 0

context(s: CtxScale)
val ctxGauge: Int get() = s.factor * 3

context(s: CtxScale)
val Int.ctxBumped: Int get() = this + s.factor

context(s: CtxScale)
var ctxGated: Int
    get() = ctxStore + s.factor
    set(v) { ctxStore = v - s.factor }

// A `toString` that takes a CONTEXT parameter is NOT the universal System.Object slot — it takes an argument on the
// CLR. The any-slot arity gate has to count the context slot; counting only the regular parameters made this
// declaration `objectOverride:true`, renaming `ToString(CtxScale)` onto System.Object's own `ToString` slot so the
// default `Any.toString()` no longer reached it. (Declaring BOTH this and an `override fun toString()` is an
// overload-resolution ambiguity the frontend rejects, so the shape below is the reachable one.)
class CtxNotAnySlot(val k: Int) {
    context(s: CtxScale)
    fun toString(): String = "k=" + (k * s.factor)
}

// --- members, inheritance -------------------------------------------------------------------------------------
class CtxHolder(val base: Int) {
    context(s: CtxScale)
    fun combine(a: Int): Int = base + a * s.factor

    context(s: CtxScale)
    val reading: Int get() = base * s.factor

    context(s: CtxScale)
    fun String.memberExt(a: Int = this.length + s.factor + base): Int = a
}

class CtxFactory {
    companion object {
        context(s: CtxScale)
        fun make(a: Int): Int = a * s.factor

        context(s: CtxScale)
        val seed: Int get() = s.factor * 7
    }
}

interface CtxShaper {
    context(s: CtxScale)
    fun shape(a: Int): Int

    context(s: CtxScale)
    fun twice(a: Int): Int = shape(a) * 2
}

class CtxShaperImpl : CtxShaper {
    context(s: CtxScale)
    override fun shape(a: Int): Int = a * s.factor
}

open class CtxBase(val k: Int) {
    context(s: CtxScale)
    open fun f(a: Int): Int = a + k + s.factor
}

class CtxDerived : CtxBase(10) {
    context(s: CtxScale)
    override fun f(a: Int): Int = super.f(a) * 2
}

// --- a lambda capturing the context parameter, and a LOCAL fun declaring one ----------------------------------
context(s: CtxScale)
fun ctxViaLambda(a: Int): Int {
    val f: (Int) -> Int = { it * s.factor }
    return f(a)
}

fun ctxLocalFun(a: Int): Int {
    context(s: CtxScale)
    fun local(x: Int): Int = x * s.factor
    return with(CtxScale(10)) { local(a) }
}

// --- context FUNCTION TYPES: the lambda's own context parameter is a physical delegate argument ---------------
fun ctxApplyFnType(f: context(CtxScale) (Int) -> Int): Int = with(CtxScale(10)) { f(5) }

// The INLINE form of the same: the splice carrier's parameter list is bound positionally to the invoke's args, so
// it must carry the lambda's OWN context parameter too.
inline fun ctxApplyFnTypeInline(f: context(CtxScale) (Int) -> Int): Int = with(CtxScale(10)) { f(5) }


class ContextParameterTests {
    @TestAttribute
    fun contextFunctions() {
        with(CtxScale(10)) {
            assertEquals(50, ctxScaled(5))                       // 50
            assertEquals(51, ctxOuter(5))                        // 51  context threaded into a nested call
            assertEquals("q:50", "q".ctxDeco(5))                 // q:50 extension receiver + context
            assertEquals(50, ctxViaContextOf(5))                 // 50  the stdlib `contextOf<T>()` context fun
            with(CtxTag("v=")) {
                assertEquals("v=50", ctxTwoContexts(5))          // v=50 two context parameters
            }
        }
        // A GENERIC context parameter — the dropped argument used to arrive as null (a silent wrong answer).
        with(CtxBox("hi")) { assertEquals("hi", ctxUnwrap<String>()) }
    }

    @TestAttribute
    fun contextParametersInDefaultArguments() {
        with(CtxScale(10)) {
            assertEquals(10, ctxDefaultOnly())                   // 10  the default reads ONLY the context
            assertEquals(15, ctxDefaultReadsBoth(5))             // 15  a + s.factor
            assertEquals(15, ctxDefaultReadsBoth(5, 15))         // 15  nothing omitted
            assertEquals(18, "abc".ctxDefaultReadsAll(5))        // 18  a + s.factor + this.length
            with(CtxTag("T")) {
                assertEquals("1/11/Tz", "z".ctxMixed())          // 1/11/Tz  all three filled
                assertEquals("1/7/Tz", "z".ctxMixed(b = 7))      // 1/7/Tz   a named MIDDLE argument
                assertEquals("2/12/X", "z".ctxMixed(2, d = "X")) // 2/12/X   omit the middle, name a later one
            }
        }
    }

    @TestAttribute
    fun contextFunctionOverloadsAndInline() {
        with(CtxScale(10)) {
            assertEquals(16, ctxOv(1, 2, 3))                     // 16  the vararg overload
            assertEquals("s=10", ctxOv("s="))                    // s=10 the String overload
            assertEquals(15, ctxInline { it + 5 })               // 15  an inline context fun with a lambda param
        }
    }

    @TestAttribute
    fun contextProperties() {
        with(CtxScale(10)) {
            assertEquals(30, ctxGauge)                           // 30  top-level context property
            assertEquals(12, 2.ctxBumped)                        // 12  extension receiver + context
            ctxGated = 250
            assertEquals(240, ctxStore)                          // 240 the setter saw the context
            assertEquals(250, ctxGated)                          // 250 and the getter round-trips it
            assertEquals(200, CtxHolder(20).reading)             // 200 member context property
            assertEquals(70, CtxFactory.seed)                    // 70  companion context property
        }
    }

    @TestAttribute
    fun contextParametersOnMembersAndOverrides() {
        with(CtxScale(10)) {
            assertEquals(70, CtxHolder(20).combine(5))           // 70  member function
            assertEquals(50, CtxFactory.make(5))                 // 50  companion function
            with(CtxHolder(7)) { assertEquals(19, "ab".memberExt()) }  // 19 dispatch + extension + context in one default
            val s: CtxShaper = CtxShaperImpl()
            assertEquals(50, s.shape(5))                         // 50  interface member, virtual dispatch
            assertEquals(100, s.twice(5))                        // 100 a default interface method calling it
            assertEquals(50, (CtxDerived() as CtxBase).f(5))     // 50  (5 + 10 + 10) * 2 via `super.f`
            // A context-parameterised `toString` does not take over System.Object's slot: `Any.toString()` still
            // reaches the default implementation (which renders the type name).
            assertEquals(true, (CtxNotAnySlot(7) as Any).toString().startsWith("CtxNotAnySlot"))  // true
        }
    }

    @TestAttribute
    fun contextParametersInCapturesAndLambdas() {
        with(CtxScale(10)) { assertEquals(50, ctxViaLambda(5)) } // 50  a lambda capturing the context parameter
        assertEquals(50, ctxLocalFun(5))                         // 50  a LOCAL fun declaring a context parameter
        // A context FUNCTION TYPE: `context(CtxScale) (Int) -> Int` is `Function2<CtxScale, Int, Int>`, so the
        // lambda's own context parameter is a physical delegate argument. It used to be dropped from the lifted
        // method's parameter list while the invoke passed it — a silently wrong result, not a verifier error.
        assertEquals(6, ctxApplyFnType { a -> a + 1 })           // 6
        // The same lambda through an INLINE callee, where the body is spliced from a carrier rather than invoked
        // through a delegate — a second parameter list that has to count the context slot.
        assertEquals(6, ctxApplyFnTypeInline { a -> a + 1 })     // 6
    }
}
