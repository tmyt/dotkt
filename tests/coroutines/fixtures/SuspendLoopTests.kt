// feature fixture — il-suspendloop: a structured loop whose body spans a suspension (#82/#98). FlattenSuspendingLoops
// desugars the loop (forArray + inline forEach-over-Iterable + counted ranges) to flat CFG so the loop temps/element
// cross the resume as SM fields; + break/continue crossing the resume. All top-level declarations use the descriptive
// `suspendLoop`/`SuspendLoop` stem so their simple names are UNIQUE across this assembly (bir2cir's cold-core
// suspend lowering keys top-level suspend funs by simple name). Driven by the shared `dotkt.support.blockOn` harness;
// the former `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import dotkt.support.blockOn

suspend fun suspendLoopAwaitDouble(x: Int): Int {
    Task.Delay(1).await()          // genuine suspension inside the loop body
    return x * 2
}

class SuspendLoopCounter(var value: Int)

suspend fun suspendLoopAwaitCount(counter: SuspendLoopCounter) {
    Task.Delay(1).await()
    counter.value++
}

suspend fun suspendLoopSumArray(xs: IntArray): Int {
    var acc = 0
    for (x in xs) {
        acc += suspendLoopAwaitDouble(x)
    }
    return acc
}

suspend fun suspendLoopSumList(xs: List<Int>): Int {
    var acc = 0
    xs.forEach { x ->
        acc += suspendLoopAwaitDouble(x)
    }
    return acc
}

suspend fun suspendLoopSumUntilNeg(xs: IntArray): Int {
    var acc = 0
    for (x in xs) {
        if (x == 0) continue
        if (x < 0) break
        acc += suspendLoopAwaitDouble(x)
    }
    return acc
}

suspend fun suspendLoopCountRangeSuspensions(): Int {
    val counter = SuspendLoopCounter(0)
    for (i in 1..3) {
        suspendLoopAwaitCount(counter)
    }
    return counter.value
}

suspend fun suspendLoopSumRange(): Int {
    var acc = 0
    for (i in 1..3) {
        acc += suspendLoopAwaitDouble(i)
    }
    return acc
}

suspend fun suspendLoopSumDescendingRange(): Int {
    var acc = 0
    for (i in 3 downTo 1) {
        acc += suspendLoopAwaitDouble(i)
    }
    return acc
}

class SuspendLoopTests {
    @TestAttribute
    fun spliceLocalSpillAcrossResume() {
        assertEquals(12, blockOn { suspendLoopSumArray(intArrayOf(1, 2, 3)) })            // 12  (2 + 4 + 6)
        assertEquals(18, blockOn { suspendLoopSumList(listOf(4, 5)) })                    // 18  (8 + 10)
        assertEquals(6, blockOn { suspendLoopSumUntilNeg(intArrayOf(1, 0, 2, -1, 9)) })   // 6   (2 + skip + 4 + break)
        assertEquals(3, blockOn { suspendLoopCountRangeSuspensions() })
        assertEquals(12, blockOn { suspendLoopSumRange() })
        assertEquals(12, blockOn { suspendLoopSumDescendingRange() })
    }
}
