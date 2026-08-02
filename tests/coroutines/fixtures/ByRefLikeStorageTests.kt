// The ACCEPTING half of the state machine's storage decision (its refusing half is tests/compile-fail/,
// Suspend*ByRefLike*). A suspend function's locals are spilled to state-machine fields only when they actually
// LIVE ACROSS a suspension; everything else stays a MoveNext local. That distinction is what lets a byref-like
// (`ref struct`) value — which the CLR refuses as an instance field — be used inside a suspend function at all.
//
// The load-bearing case is `byRefStorageLoopScoped`: the Span is created and consumed inside each iteration of a loop
// whose body ALSO suspends. A lexical "declared before a suspension, read after one" interval would reject it
// (the declaration precedes the suspension and a read follows one, on the next iteration); real liveness accepts
// it, because on every path from the suspension to a read there is a fresh definition first. Its twin with the
// value carried ACROSS the back edge is the compile-fail case SuspendLoopCarriedByRefLike.kt.
//
// All top-level declarations use the `byRefStorage`/`ByRefStorage` feature stem so their simple names are
// unique across this assembly (the cold-core lowering keys top-level suspend funs by simple name).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Span
import System.Threading.Tasks.Task
import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine
import dotkt.support.blockOn

suspend fun byRefStorageTick(n: Int): Int {
    Task.Delay(1).await()
    return n + 1
}

// The regression guard against an interval approximation: byref-like, declared inside a suspending loop body,
// dead across the suspension on every path, so it stays a local.
suspend fun byRefStorageLoopScoped(): Int {
    var total = 0
    for (i in 0 until 3) {
        val s = Span<Int>(arrayOf(1, 2, 3))
        total += s.Length
        total += byRefStorageTick(i)
    }
    return total
}

// Straight-line dead-across: consumed BEFORE the suspension, so no field is minted for it either.
suspend fun byRefStorageBeforeSuspension(): Int {
    val s = Span<Int>(arrayOf(1, 2, 3, 4))
    val len = s.Length
    return len + byRefStorageTick(len)
}

// Created AFTER the last suspension — the resume point precedes the declaration entirely.
suspend fun byRefStorageAfterSuspension(): Int {
    val t = byRefStorageTick(1)
    val s = Span<Int>(arrayOf(1, 2))
    return t + s.Length
}

// Trivial path 1: a plain (non-suspend) function has no state machine at all.
fun byRefStoragePlain(): Int {
    val s = Span<Int>(arrayOf(1, 2, 3, 4, 5))
    var n = 0
    for (i in 0 until s.Length) n += 1
    return n
}

// Trivial path 2: a suspend function with NO suspension point has no state machine at all — its body stays in
// the cold entry's own frame, so a byref-like local is as ordinary there as in a plain function. (Its ABI is a
// different question: a byref-like PARAMETER or RESULT is refused for every suspend declaration, suspending or
// not — see tests/compile-fail/SuspendFreeParameterByRefLike.kt.)
suspend fun byRefStorageNoSuspension(): Int {
    val s = Span<Int>(arrayOf(1, 2, 3))
    return s.Length
}

// `suspendCoroutine { … }` materializes its block as a CAPTURING closure in BIR, but the cold lowering
// reconstructs that block INLINE and deletes the closure class — so the "capture" is really a local of this
// frame, consumed before the intrinsic suspension. Refusing it as a closure capture (the CS8352 mirror) would
// reject a program that emits no closure class at all. Its live-across sibling is refused by the ordinary
// storage gate instead: tests/compile-fail/SuspendCoroutineBlockByRefLikeLive.kt.
suspend fun byRefStorageIntrinsicBlock(): Int {
    val s = Span<Int>(arrayOf(1, 2, 3))
    return suspendCoroutine { c -> c.resume(s.Length) }
}

class ByRefLikeStorageTests {
    @TestAttribute
    fun byRefLikeLocalScopedToALoopIterationStaysALocal() {
        // 3 iterations x (Span length 3 + tick(i) = i + 1) = 9 + (1 + 2 + 3) = 15
        assertEquals(15, blockOn { byRefStorageLoopScoped() })
    }

    @TestAttribute
    fun byRefLikeLocalDeadBeforeAndAfterASuspension() {
        assertEquals(9, blockOn { byRefStorageBeforeSuspension() })    // 4 + tick(4) = 4 + 5
        assertEquals(4, blockOn { byRefStorageAfterSuspension() })     // tick(1) = 2, + 2
    }

    @TestAttribute
    fun byRefLikeSurvivesAnInlinedSuspendCoroutineBlock() {
        assertEquals(3, blockOn { byRefStorageIntrinsicBlock() })
    }

    @TestAttribute
    fun byRefLikeStaysLegalOnTheNonSuspendingPaths() {
        assertEquals(5, byRefStoragePlain())
        assertEquals(3, blockOn { byRefStorageNoSuspension() })
    }
}
