// CAPTURE CONTROL ON AWAITABLES THAT ARE NOT TASK (GitHub #64). `await(captureContext = …)` lowers to
// `awaitable.ConfigureAwait(<value>).GetAwaiter()`, and everything about that hop — the configured type, how it is
// entered, and where the awaitable is held while the argument runs — is read from the referenced metadata. Task
// answers all three questions the same easy way, so these three producer types (tests/interop/producer/
// CaptureAwaitable.cs) ask them differently:
//
//   1. `Pair<A,B>.ConfigureAwait(bool): ConfiguredPair<B,A>` PERMUTES its type arguments. A lowering that rebuilds
//      the configured type from the RECEIVER's arguments emits `ConfiguredPair<Int,String>` — a real type on which
//      none of the members it then calls exist, so the assembly fails verification and the call fails at run time.
//      Both arms are covered, because the constant `false` reached the same malformed lowering before the dynamic
//      one was allowed to exist.
//   2. `Duo<A,B>`'s configured awaitable has NO member GetAwaiter — only a referenced generic `[Extension]` one,
//      whose type arguments have to be unified from its declared receiver (which is permuted too). Requesting
//      capture control on it used to be refused outright, though C# `await` compiles the same shape.
//   3. `RefTick` is a `ref struct`. The CLR forbids one as a FIELD, never as a local, and only a suspension makes
//      the awaitable's binding a field — so a captureContext argument that merely transfers control must not turn
//      an awaitable the language allows into a compile-time refusal.
//
// The awaiter is what crosses a suspension in all three, and it is an ordinary struct in all three.
import CaptureAwaitable.Duo
import CaptureAwaitable.NestedAwaitable
import CaptureAwaitable.Pair
import CaptureAwaitable.RefTickApi
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import dotkt.support.blockOn

// ---- 1. permuted configured type ------------------------------------------------------------------------------
private suspend fun capPermuted(a: Int, b: String, synchronous: Boolean, capture: Boolean): Int =
    Pair<Int, String>(a, b, synchronous).await(captureContext = capture)

private suspend fun capPermutedConstFalse(a: Int, b: String, synchronous: Boolean): Int =
    Pair<Int, String>(a, b, synchronous).await(captureContext = false)

private suspend fun capPermutedPlain(a: Int, b: String, synchronous: Boolean): Int =
    Pair<Int, String>(a, b, synchronous).await()

// ---- 2. configured awaitable entered through a referenced extension GetAwaiter ---------------------------------
private suspend fun capExtensionConfigured(a: Int, b: String, synchronous: Boolean, capture: Boolean): Int =
    Duo<Int, String>(a, b, synchronous).await(captureContext = capture)

// ---- 3. byref-like awaitable ----------------------------------------------------------------------------------
private suspend fun capByRefLikeEscaping(v: Int, bail: Boolean): Int =
    RefTickApi.Make(v).await(captureContext = if (bail) throw IllegalStateException("bail") else false)

private suspend fun capByRefLikeDynamic(v: Int, capture: Boolean): Int =
    RefTickApi.Make(v).await(captureContext = capture)

// ---- 4. nested generic awaiter --------------------------------------------------------------------------------
private suspend fun capNestedAwaiter(v: Int, synchronous: Boolean): Int =
    NestedAwaitable<Int>(v, synchronous).await()

class CaptureContextAwaitTests {
    // The value comes back through the configured awaiter's `GetResult()`, which is only reachable if the configured
    // type was named as DECLARED — the permuted one.
    @TestAttribute
    fun permutedConfiguredTypeIsNamedAsDeclared() {
        assertEquals(11, blockOn { capPermuted(11, "x", true, true) })     // synchronous fast path
        assertEquals(12, blockOn { capPermuted(12, "x", true, false) })
        assertEquals(13, blockOn { capPermuted(13, "x", false, false) })   // genuine suspension
        assertEquals(14, blockOn { capPermuted(14, "x", false, true) })
    }

    // The constant arm reaches the same lowering, and reached the same defect before it did.
    @TestAttribute
    fun permutedConfiguredTypeForAConstantArgument() {
        assertEquals(15, blockOn { capPermutedConstFalse(15, "x", true) })
        assertEquals(16, blockOn { capPermutedConstFalse(16, "x", false) })
    }

    // Capture control changes nothing about the plain path, which never constructs the configured awaitable.
    @TestAttribute
    fun plainAwaitIsUnaffected() {
        assertEquals(17, blockOn { capPermutedPlain(17, "x", true) })
        assertEquals(18, blockOn { capPermutedPlain(18, "x", false) })
    }

    // A configured awaitable whose only GetAwaiter is a referenced generic extension.
    @TestAttribute
    fun configuredAwaitableEnteredThroughAnExtensionGetAwaiter() {
        assertEquals(21, blockOn { capExtensionConfigured(21, "y", true, false) })
        assertEquals(22, blockOn { capExtensionConfigured(22, "y", false, true) })
    }

    // A byref-like awaitable with a captureContext argument that emits statements but never suspends: the awaitable
    // is held in a local, which is legal for it, rather than in a state-machine field, which is not.
    @TestAttribute
    fun byRefLikeAwaitableWithAControlTransferringArgument() {
        assertEquals(31, blockOn { capByRefLikeEscaping(31, false) })

        var message: String? = null
        try {
            blockOn { capByRefLikeEscaping(32, true) }
        } catch (e: IllegalStateException) {
            message = e.message
        }
        assertEquals("bail", message)

        assertEquals(33, blockOn { capByRefLikeDynamic(33, true) })
        assertEquals(34, blockOn { capByRefLikeDynamic(34, false) })
    }

    @TestAttribute
    fun nestedGenericAwaiterKeepsEachSegmentArity() {
        assertEquals(41, blockOn { capNestedAwaiter(41, true) })
        assertEquals(42, blockOn { capNestedAwaiter(42, false) })
    }
}
