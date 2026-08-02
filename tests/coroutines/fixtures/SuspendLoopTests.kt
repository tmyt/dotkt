// feature fixture — il-suspendloop: a structured loop whose body spans a suspension (#82/#98). FlattenSuspendingLoops
// desugars the loop (forArray + inline forEach-over-Iterable + counted ranges) to flat CFG so the loop temps/element
// cross the resume as SM fields; + break/continue crossing the resume. All top-level decls carry the `slp` case token
// under the shared `suspendLoop`/`SuspendLoop` prefix so their simple names are UNIQUE across this assembly (bir2cir's cold-core
// suspend lowering keys top-level suspend funs by simple name). Driven by the shared `dotkt.support.blockOn` harness;
// the former `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import dotkt.support.blockOn

suspend fun suspendLoopSlpAwaitDouble(x: Int): Int {
    Task.Delay(1).await()          // genuine suspension inside the loop body
    return x * 2
}

class SuspendLoopSlpCounter(var value: Int)

suspend fun suspendLoopSlpAwaitCount(counter: SuspendLoopSlpCounter) {
    Task.Delay(1).await()
    counter.value++
}

suspend fun suspendLoopSlpSumArray(xs: IntArray): Int {
    var acc = 0
    for (x in xs) {
        acc += suspendLoopSlpAwaitDouble(x)
    }
    return acc
}

suspend fun suspendLoopSlpSumList(xs: List<Int>): Int {
    var acc = 0
    xs.forEach { x ->
        acc += suspendLoopSlpAwaitDouble(x)
    }
    return acc
}

suspend fun suspendLoopSlpSumUntilNeg(xs: IntArray): Int {
    var acc = 0
    for (x in xs) {
        if (x == 0) continue
        if (x < 0) break
        acc += suspendLoopSlpAwaitDouble(x)
    }
    return acc
}

suspend fun suspendLoopSlpCountRangeSuspensions(): Int {
    val counter = SuspendLoopSlpCounter(0)
    for (i in 1..3) {
        suspendLoopSlpAwaitCount(counter)
    }
    return counter.value
}

suspend fun suspendLoopSlpSumRange(): Int {
    var acc = 0
    for (i in 1..3) {
        acc += suspendLoopSlpAwaitDouble(i)
    }
    return acc
}

suspend fun suspendLoopSlpSumDescendingRange(): Int {
    var acc = 0
    for (i in 3 downTo 1) {
        acc += suspendLoopSlpAwaitDouble(i)
    }
    return acc
}

class SuspendLoopTests {
    @TestAttribute
    fun spliceLocalSpillAcrossResume() {
        assertEquals(12, blockOn { suspendLoopSlpSumArray(intArrayOf(1, 2, 3)) })            // 12  (2 + 4 + 6)
        assertEquals(18, blockOn { suspendLoopSlpSumList(listOf(4, 5)) })                    // 18  (8 + 10)
        assertEquals(6, blockOn { suspendLoopSlpSumUntilNeg(intArrayOf(1, 0, 2, -1, 9)) })   // 6   (2 + skip + 4 + break)
        assertEquals(3, blockOn { suspendLoopSlpCountRangeSuspensions() })
        assertEquals(12, blockOn { suspendLoopSlpSumRange() })
        assertEquals(12, blockOn { suspendLoopSlpSumDescendingRange() })
    }
}
