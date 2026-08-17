// Void-to-value DELEGATE ADAPTATION (#400 §7). A Kotlin `Unit` lambda lowers to a void-returning delegate, but a
// .NET delegate slot can declare an `Invoke` that returns — a generic `Func<T,R>` instantiated at Unit, or a custom
// delegate returning `object`. No method pointer is delegate-compatible with such a slot (`void` is assignable to
// nothing), so bir2cir authors an adapter that calls the natural delegate and returns the `Unit` singleton.
//
// The battery covers the axes the adaptation is a function of: the delegate's ARITY (0, 1, 2); the natural
// delegate's TARGET (a non-capturing lambda's static target vs a capturing lambda's closure instance); the generic
// FRAME the site sits in (none, a generic method, a generic OWNER with a constrained parameter); a BYREF-LIKE
// parameter, which a delegate family admits and an adapter parameter standing for it must admit too; and the KIND
// of slot being filled (an argument's parameter, a property setter's parameter, a public delegate field). The
// transpose — a value-returning lambda meeting a `void` Invoke — is here too, because the Kotlin coercion to Unit
// makes it the SAME construction with nothing to produce, and it must stay a plain retarget.
//
// Every case asserts an OBSERVABLE effect of actually invoking the delegate as well as the returned value, so a
// well-formed but non-executing adapter cannot pass.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import Cbk.Constrained
import Cbk.GenericCallbacks
import Cbk.IMarker
import Cbk.Marker
import CbkUnit.ConstrainedHost
import CbkUnit.DelegateStore
import CbkUnit.UnitCallbacks
import CbkUnit.UnitTarget

private class KotlinUnitTarget {
    var marks: Int = 0
    fun mark() { marks++ }
}

// Arity 0, CAPTURING: the natural `Action` is built from a closure instance, and the adapter holds that value.
private fun nullaryCapturingUnitDelegate(log: StringBuilder): Any? =
    UnitCallbacks.UseNullary({ log.append("zero"); Unit })

// Arity 1, NON-capturing: the natural `Action<T>` binds a lifted static target directly.
private fun <T> unaryStaticUnitDelegate(value: T): Any? =
    GenericCallbacks.UseResult<T>({ _: T -> Unit }, value)

// Arity 2 inside a generic METHOD, capturing the enclosing frame: the adapter is instantiated at the frame's own
// type variable, and its `invoke` takes both delegate parameters.
private fun <T> binaryCapturingUnitDelegate(first: T, log: StringBuilder): Any? =
    UnitCallbacks.UseBinary<T>({ a: T, b: String -> log.append(b).append(a.toString()); Unit }, first, "b")

// A generic OWNER whose parameter is CONSTRAINED. The adapter is instantiated at `Constrained<T>` from inside a
// frame whose `T : IMarker`; the adapter itself needs no positive constraint, because its parameter is the
// delegate's whole parameter type rather than that type's own argument, and the bound on that argument was
// already satisfied where the frame wrote it.
private fun <T : IMarker> constrainedGenericOwnerUnitDelegate(
    host: ConstrainedHost<T>, value: Constrained<T>, log: StringBuilder): Any? =
    host.Use({ _: Constrained<T> -> log.append("owner"); Unit }, value)

// A BYREF-LIKE delegate parameter. The natural `Action<Span<Int>>` is legal because the delegate family's own
// parameter admits a ref struct, so the adapter's parameter — which stands for exactly that one — has to admit the
// same instantiation or the adapter class cannot be constructed at all.
private fun spanParameterUnitDelegate(log: StringBuilder): Any? =
    UnitCallbacks.UseSpan({ s -> log.append(UnitCallbacks.SpanTotal(s)); Unit }, intArrayOf(1, 2, 3))

// The transpose: a VALUE-returning lambda meeting a `void` Invoke. Kotlin coerces the lambda to Unit, so the
// natural delegate is already void-returning and the construction is retargeted to the custom delegate, never
// adapted.
private fun valueLambdaIntoVoidDelegate(log: StringBuilder): String =
    UnitCallbacks.UseSink({ v: Int -> log.append(v) })

// A delegate slot is not only a parameter. A .NET property SETTER's parameter and a public delegate FIELD are
// declared slots too, and a literal lambda stored into one must construct that slot's delegate — including the
// adapted Unit form, where the value the getter side observes is the Unit singleton and nothing else.
private fun storedDelegateSlots(log: StringBuilder): String {
    val store = DelegateStore()
    store.Sink = { v: Int -> log.append(v) }          // custom void delegate, from a value-returning lambda
    store.SinkField = { v: Int -> log.append(v * 2) } // the same slot, reached as a public field
    store.Valued = { log.append("v"); Unit }          // Invoke RETURNS: the adapted form
    val sunk = store.RunSink(3)
    val fielded = store.RunSinkField(4)
    val valued = store.RunValued()
    return sunk + "|" + fielded + "|" + (valued === Unit) + "|" + log.toString()
}

class UnitDelegateAdapterTests {
    @TestAttribute
    fun callableReferencesAlsoFillTheDeclaredDelegateSlot() {
        val kotlin = KotlinUnitTarget()
        assertEquals(Unit, UnitCallbacks.UseNullary(kotlin::mark))
        assertEquals(1, kotlin.marks)

        val clr = UnitTarget()
        assertEquals(Unit, UnitCallbacks.UseNullary(clr::Mark))
        assertEquals(1, clr.Marks)

        UnitCallbacks.ResetStaticMarks()
        assertEquals(Unit, UnitCallbacks.UseNullary(UnitCallbacks::MarkStatic))
        assertEquals(1, UnitCallbacks.StaticMarks)
    }

    @TestAttribute
    fun nullaryCapturingLambdaFillsAValueReturningDelegate() {
        val log = StringBuilder()
        assertEquals(Unit, nullaryCapturingUnitDelegate(log))
        assertEquals("zero", log.toString())
    }

    @TestAttribute
    fun unaryStaticTargetLambdaFillsAValueReturningDelegate() {
        assertEquals(Unit, unaryStaticUnitDelegate(11))
        assertEquals(Unit, unaryStaticUnitDelegate("s"))
    }

    @TestAttribute
    fun binaryCapturingLambdaInAGenericFrameFillsAValueReturningDelegate() {
        val log = StringBuilder()
        assertEquals(Unit, binaryCapturingUnitDelegate(41, log))
        assertEquals("b41", log.toString())
    }

    @TestAttribute
    fun constrainedGenericOwnerFillsAValueReturningDelegate() {
        val log = StringBuilder()
        val host = ConstrainedHost<Marker>()
        assertEquals(Unit, constrainedGenericOwnerUnitDelegate(host, Constrained<Marker>(Marker()), log))
        assertEquals("owner", log.toString())
    }

    @TestAttribute
    fun byRefLikeDelegateParameterFillsAValueReturningDelegate() {
        val log = StringBuilder()
        assertEquals(Unit, spanParameterUnitDelegate(log))
        assertEquals("6", log.toString())
    }

    @TestAttribute
    fun literalLambdaStoredIntoADelegatePropertyAndField() {
        // Boolean.toString() is the CLR-native "True"/"False" (docs/dotkt-semantics.md), not Kotlin/JVM lowercase.
        assertEquals("sink|field|True|38v", storedDelegateSlots(StringBuilder()))
    }

    @TestAttribute
    fun valueReturningLambdaFillsAVoidDelegate() {
        val log = StringBuilder()
        assertEquals("sunk", valueLambdaIntoVoidDelegate(log))
        assertEquals("7", log.toString())
    }
}
