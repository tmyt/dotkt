// CorB batch — il-suspendloop: a structured loop whose body spans a suspension (#82/#98). FlattenSuspendingLoops
// desugars the loop (forArray + inline forEach-over-Iterable + counted ranges) to flat CFG so the loop temps/element
// cross the resume as SM fields; + break/continue crossing the resume. All top-level decls carry the `slp` case token
// under the shared `corB`/`CorB` prefix so their simple names are UNIQUE across this assembly (bir2cir's cold-core
// suspend lowering keys top-level suspend funs by simple name). Driven by the shared `dotkt.support.blockOn` harness;
// the former `main` + golden -> one @TestAttribute method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import System.Threading.Tasks.Task
import dotkt.support.blockOn

suspend fun corBSlpAwaitDouble(x: Int): Int {
    Task.Delay(1).await()          // genuine suspension inside the loop body
    return x * 2
}

class CorBSlpCounter(var value: Int)

suspend fun corBSlpAwaitCount(counter: CorBSlpCounter) {
    Task.Delay(1).await()
    counter.value++
}

suspend fun corBSlpSumArray(xs: IntArray): Int {
    var acc = 0
    for (x in xs) {
        acc += corBSlpAwaitDouble(x)
    }
    return acc
}

suspend fun corBSlpSumList(xs: List<Int>): Int {
    var acc = 0
    xs.forEach { x ->
        acc += corBSlpAwaitDouble(x)
    }
    return acc
}

suspend fun corBSlpSumUntilNeg(xs: IntArray): Int {
    var acc = 0
    for (x in xs) {
        if (x == 0) continue
        if (x < 0) break
        acc += corBSlpAwaitDouble(x)
    }
    return acc
}

suspend fun corBSlpCountRangeSuspensions(): Int {
    val counter = CorBSlpCounter(0)
    for (i in 1..3) {
        corBSlpAwaitCount(counter)
    }
    return counter.value
}

suspend fun corBSlpSumRange(): Int {
    var acc = 0
    for (i in 1..3) {
        acc += corBSlpAwaitDouble(i)
    }
    return acc
}

suspend fun corBSlpSumDescendingRange(): Int {
    var acc = 0
    for (i in 3 downTo 1) {
        acc += corBSlpAwaitDouble(i)
    }
    return acc
}

class SuspendLoopTests {
    @TestAttribute
    fun spliceLocalSpillAcrossResume() {
        assertEquals(12, blockOn { corBSlpSumArray(intArrayOf(1, 2, 3)) })            // 12  (2 + 4 + 6)
        assertEquals(18, blockOn { corBSlpSumList(listOf(4, 5)) })                    // 18  (8 + 10)
        assertEquals(6, blockOn { corBSlpSumUntilNeg(intArrayOf(1, 0, 2, -1, 9)) })   // 6   (2 + skip + 4 + break)
        assertEquals(3, blockOn { corBSlpCountRangeSuspensions() })
        assertEquals(12, blockOn { corBSlpSumRange() })
        assertEquals(12, blockOn { corBSlpSumDescendingRange() })
    }
}
