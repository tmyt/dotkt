// A BYREF-LIKE (`ref struct`) value at a call that FILLS a default argument.
//
// The single-evaluation pre-pass binds a call's supplied values to `var` temporaries so a filled default reads the
// binding rather than a second rendering. A `var` is not merely a local: bir2cir's SuspendColdLowering promotes every
// non-handler `var` of a coroutine body to an INSTANCE FIELD of the state machine, and the CLR rejects a type with a
// byref-like instance field at LOAD time — "A ByRef or ByRef-like type cannot be used as the type for an instance field
// in a non-ByRef-like type". So a byref-like value is never bound, and neither is any other SUPPLIED value of that call
// (a partial hoist would move them across it). The filled default a later default reads still binds, because that temp
// is what makes the two readers see one value.
//
// This lane is the one that can express BOTH halves: `../producer` supplies the byref-like .NET surface, and the
// coroutine-support ProjectReference supplies `blockOn`, so the temp really does land in a state machine.
//
// The shape each case needs: a callee whose LATER default reads an EARLIER non-stable default, which is what extends
// the binding range through the LAST supplied argument and so reaches the byref-like one.
import ByRefLikeInterop.ByRefLikeApi
import ByRefLikeInterop.Tally
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.ReadOnlySpan
import dotkt.support.blockOn
import kotlin.clr.Span

// The order log: `T` marks the supplied argument's evaluation, `d` each evaluation of the first default. `brlBump` is
// STATEFUL on purpose — a default that returned the same number every time would hide a second evaluation behind a
// correct-looking result, which is exactly the mistake the chained default (`b = a * 10`) exists to catch.
var brlLog = ""
var brlSeq = 0
fun brlMark(v: Int): Int { brlLog += "T"; return v }
fun brlBump(): Int { brlLog += "d"; brlSeq++; return brlSeq + 2 }

// A user-defined `ref struct` — byref-like without being Span/ReadOnlySpan.
private fun brlTally(t: Tally, a: Int = brlBump(), b: Int = a * 10): Int = ByRefLikeApi.ReadTally(t) + b
// A BCL byref-like reached through an ordinary .NET signature.
private fun brlSpanChars(s: ReadOnlySpan<Char>, a: Int = brlBump(), b: Int = a * 10): Int =
    ByRefLikeApi.CharsLength(s) + b
// `kotlin.clr.Span<T>` — the kotc INTRINSIC spelling of System.Span<T>, which facadegen never emits a record for.
private fun brlSpanInts(s: Span<Int>, a: Int = brlBump(), b: Int = a * 10): Int = ByRefLikeApi.SpanLength(s) + b
// A byref-like EXTENSION RECEIVER: bound by the same pre-pass, under the same restriction.
private fun ReadOnlySpan<Char>.brlExt(a: Int = brlBump(), b: Int = a * 10): Int =
    ByRefLikeApi.CharsLength(this) + b

private suspend fun brlRelay(): Int = 5

private suspend fun brlSuspendTally(): Int = brlTally(ByRefLikeApi.MakeTally(brlMark(4))) + brlRelay()
private suspend fun brlSuspendChars(): Int = brlSpanChars(ByRefLikeApi.Chars("hello")) + brlRelay()
private suspend fun brlSuspendSpan(): Int = brlSpanInts(ByRefLikeApi.MakeSpan(intArrayOf(1, 2, 3, 4))) + brlRelay()
private suspend fun brlSuspendReceiver(): Int = ByRefLikeApi.Chars("hello").brlExt() + brlRelay()

// The call sits in a NON-suspend inline lambda, which `run` splices into the SUSPEND caller's frame — so the temp's
// storage is decided by the body it LANDS in, not by the lexically enclosing function.
// The `run { … }` result goes through a local ON PURPOSE: writing it as an operand
// (`run { … } + brlRelay()`) hits an UNRELATED pre-existing miscompile — an inline lambda whose body is a CALL, used
// as the left operand of an add across a suspension, spills as an unconverted `object`
// (`AccessViolationException`, and an `ExpectedNumericType` ILVerify finding). Reproduced on `origin/main` with no
// default argument and no byref-like type in sight: `suspend fun f(): Int = run { plain() } + relay()`.
private suspend fun brlSuspendInlined(): Int {
    val v = run { brlTally(ByRefLikeApi.MakeTally(brlMark(4))) }
    return v + brlRelay()
}

class ByRefLikeSingleEvalTests {
    // Each of these emitted a state machine that failed to LOAD before the byref-like value was excluded from binding.
    // The VALUE assertions are the load-bearing ones: `a` must be evaluated once and `b` must read THAT value, so with
    // `brlBump()` returning 3 on its first call the result is `4 + 30`. A second evaluation of `a` would make `b` 40.
    @TestAttribute
    fun refStructArgumentSurvivesASuspension() {
        brlLog = ""; brlSeq = 0
        assertEquals(39, blockOn { brlSuspendTally() })            // 4 + (3 * 10) + 5
        assertEquals(1, brlSeq)                                    // the default ran ONCE
        // `d` before `T`: the fill's temp is declared ahead of the call node, so it runs before the supplied argument.
        // Kotlin's order is the other way round, and this is the ONE shape where the two cannot both be had — the
        // byref-like argument cannot join the temps, so binding the fill necessarily jumps it. Pre-existing (this is
        // exactly what `origin/main` does); the alternative — dropping the fill's temp — makes `a` and `b` see
        // DIFFERENT values, which is strictly worse.
        assertEquals("dT", brlLog)
    }

    @TestAttribute
    fun bclByRefLikeArgumentSurvivesASuspension() {
        brlSeq = 0
        assertEquals(40, blockOn { brlSuspendChars() })            // 5 + (3 * 10) + 5
        assertEquals(1, brlSeq)
    }

    @TestAttribute
    fun spanIntrinsicArgumentSurvivesASuspension() {
        brlSeq = 0
        assertEquals(39, blockOn { brlSuspendSpan() })             // 4 + (3 * 10) + 5
        assertEquals(1, brlSeq)
    }

    @TestAttribute
    fun byRefLikeExtensionReceiverSurvivesASuspension() {
        brlSeq = 0
        assertEquals(40, blockOn { brlSuspendReceiver() })         // 5 + (3 * 10) + 5
        assertEquals(1, brlSeq)
    }

    @TestAttribute
    fun byRefLikeInAnInlinedBodySurvivesTheCallersSuspension() {
        brlSeq = 0
        assertEquals(39, blockOn { brlSuspendInlined() })          // 4 + (3 * 10) + 5
        assertEquals(1, brlSeq)
    }

    // The control: the same call OUTSIDE a coroutine. A byref-like local is legal there, so this one never broke —
    // it pins that excluding the value from binding changed neither its result nor its default's evaluation count.
    @TestAttribute
    fun byRefLikeArgumentInAnOrdinaryFunction() {
        brlLog = ""; brlSeq = 0
        assertEquals(34, brlTally(ByRefLikeApi.MakeTally(brlMark(4))))
        assertEquals(1, brlSeq)
        assertEquals("dT", brlLog)
    }
}
