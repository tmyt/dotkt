// A BYREF-LIKE (`ref struct`) value at a call that FILLS a default argument.
//
// Every value a call supplies is a binding of its evaluation plan (docs/bir-cir-spec.md §2.7) — mandatory, not an
// optimisation — so a byref-like argument is bound like any other value and Kotlin's order is never traded for it.
// What byref-like-ness decides is only the PHYSICAL form of that binding, and that is decided once, later, by
// liveness: a value dead before every suspension stays a MoveNext local, where a `ref struct` is perfectly legal, and
// only a value that must genuinely SURVIVE a suspension is refused — a compile error, the CS4007 mirror, covered by
// tests/compile-fail. None of the calls below need that: the byref-like value is consumed by the call itself, so it
// dies before the suspension that follows.
//
// This lane is the one that can express BOTH halves: `../producer` supplies the byref-like .NET surface, and the
// coroutine-support ProjectReference supplies `blockOn`, so the value really does land in a state machine.
//
// The shape each case needs: a callee whose LATER default reads an EARLIER non-stable default, so the filled default
// has two readers and must become a local — which is what puts a materialised binding after the byref-like argument
// and makes the order observable.
import ByRefLikeInterop.ByRefLikeApi
import ByRefLikeInterop.StorageCollision
import ByRefLikeInterop.Tally
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
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
// A BCL byref-like arriving through an ordinary .NET signature.
private fun brlSpanChars(s: ReadOnlySpan<Char>, a: Int = brlBump(), b: Int = a * 10): Int =
    ByRefLikeApi.CharsLength(s) + b
// `kotlin.clr.Span<T>` — the kotc INTRINSIC spelling of System.Span<T>, which dll2klib never emits a record for.
private fun brlSpanInts(s: Span<Int>, a: Int = brlBump(), b: Int = a * 10): Int = ByRefLikeApi.SpanLength(s) + b
// A byref-like EXTENSION RECEIVER: bound by the same plan, under the same rule.
private fun ReadOnlySpan<Char>.brlExt(a: Int = brlBump(), b: Int = a * 10): Int =
    ByRefLikeApi.CharsLength(this) + b

private suspend fun brlRelay(): Int = 5

private suspend fun brlSameStemHeapClass(): Int {
    val value = StorageCollision(37)
    val after = brlRelay()
    return value.Value + after
}

private suspend fun brlSuspendTally(): Int = brlTally(ByRefLikeApi.MakeTally(brlMark(4))) + brlRelay()
private suspend fun brlSuspendChars(): Int = brlSpanChars(ByRefLikeApi.Chars("hello")) + brlRelay()
private suspend fun brlSuspendSpan(): Int = brlSpanInts(ByRefLikeApi.MakeSpan(intArrayOf(1, 2, 3, 4))) + brlRelay()
private suspend fun brlSuspendReceiver(): Int = ByRefLikeApi.Chars("hello").brlExt() + brlRelay()

// A byref-like value held in a NAMED LOCAL across the call, so the plan's binding is not the only local of that type
// in the body: the liveness verdict has to be per-VALUE, not per-type.
private suspend fun brlSuspendNamedVal(): Int {
    val chars = ByRefLikeApi.Chars("hello")
    val n = brlSpanChars(chars)
    return n + brlRelay()
}

// The call sits in a NON-suspend inline lambda, which `run` splices into the SUSPEND caller's frame — so the storage
// of the plan's locals is decided by the body they LAND in, not by the lexically enclosing function.
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
    @TestAttribute
    fun genericRefStructDoesNotPoisonSameStemHeapClass() {
        assertEquals(42, blockOn { brlSameStemHeapClass() })
    }

    // Each of these emitted a state machine that failed to LOAD while every `var` of a coroutine body became an SM
    // field. The VALUE assertions are the load-bearing ones: `a` must be evaluated once and `b` must read THAT value,
    // so with `brlBump()` returning 3 on its first call the result is `4 + 30`. A second evaluation of `a` would make
    // `b` 40.
    @TestAttribute
    fun refStructArgumentSurvivesASuspension() {
        brlLog = ""; brlSeq = 0
        assertEquals(39, blockOn { brlSuspendTally() })            // 4 + (3 * 10) + 5
        assertEquals(1, brlSeq)                                    // the default ran ONCE
        // `T` before `d`: Kotlin evaluates the supplied argument, then the callee's defaults. The byref-like argument
        // is a plan binding like every other value, so binding the fill cannot jump it. (This asserted the reverse,
        // `"dT"`, while its own comment stated that Kotlin required this order — the compromise it recorded was a
        // property of the old two-representation shape, and there is no longer a choice to make.)
        assertEquals("Td", brlLog)
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
    fun byRefLikeNamedValIsDeadBeforeTheSuspension() {
        brlSeq = 0
        assertEquals(40, blockOn { brlSuspendNamedVal() })         // 5 + (3 * 10) + 5
        assertEquals(1, brlSeq)
    }

    @TestAttribute
    fun byRefLikeInAnInlinedBodySurvivesTheCallersSuspension() {
        brlSeq = 0
        assertEquals(39, blockOn { brlSuspendInlined() })          // 4 + (3 * 10) + 5
        assertEquals(1, brlSeq)
    }

    // The control: the same call OUTSIDE a coroutine. A byref-like local is legal there unconditionally, so this one
    // pins that the ORDER is identical whether or not a state machine is involved — the plan is a semantic fact, and
    // storage is a separate decision that never reaches back into it.
    @TestAttribute
    fun byRefLikeArgumentInAnOrdinaryFunction() {
        brlLog = ""; brlSeq = 0
        assertEquals(34, brlTally(ByRefLikeApi.MakeTally(brlMark(4))))
        assertEquals(1, brlSeq)
        assertEquals("Td", brlLog)
    }
}
