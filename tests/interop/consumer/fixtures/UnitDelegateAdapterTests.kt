// Void-to-value DELEGATE ADAPTATION (#400 §7). A Kotlin `Unit` lambda lowers to a void-returning delegate, but a
// .NET delegate slot can declare an `Invoke` that returns — a generic `Func<T,R>` instantiated at Unit, or a custom
// delegate returning `object`. No method pointer is delegate-compatible with such a slot (`void` is assignable to
// nothing), so bir2cir authors an adapter that calls the natural delegate and returns the `Unit` singleton.
//
// The battery covers the axes the adaptation is a function of: the delegate's ARITY (0, 1, 2), the natural
// delegate's TARGET (a non-capturing lambda's static target vs a capturing lambda's closure instance), and the
// generic FRAME the site sits in (none, a generic method, a generic OWNER with a constrained parameter). The
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
import CbkUnit.UnitCallbacks

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
// frame whose `T : IMarker`; the adapter itself needs no constraint, because its parameter is the delegate's whole
// parameter type rather than that type's own argument.
private fun <T : IMarker> constrainedGenericOwnerUnitDelegate(
    host: ConstrainedHost<T>, value: Constrained<T>, log: StringBuilder): Any? =
    host.Use({ _: Constrained<T> -> log.append("owner"); Unit }, value)

// The transpose: a VALUE-returning lambda meeting a `void` Invoke. Kotlin coerces the lambda to Unit, so the
// natural delegate is already void-returning and the construction is retargeted to the custom delegate, never
// adapted.
private fun valueLambdaIntoVoidDelegate(log: StringBuilder): String =
    UnitCallbacks.UseSink({ v: Int -> log.append(v) })

class UnitDelegateAdapterTests {
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
    fun valueReturningLambdaFillsAVoidDelegate() {
        val log = StringBuilder()
        assertEquals("sunk", valueLambdaIntoVoidDelegate(log))
        assertEquals("7", log.toString())
    }
}
