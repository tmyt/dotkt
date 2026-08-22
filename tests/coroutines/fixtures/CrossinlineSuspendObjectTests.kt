// feature fixture — il-inlsuspendobj: the FORMER SILENT-MISCOMPILE cell — a `crossinline` SUSPEND lambda captured by an
// `object :` literal inside an inline body, materialized §4.4ii into a real newSuspendLambda VALUE. All decls carry
// the descriptive `crossinlineObject`/`CrossinlineObject` stem so their simple names are UNIQUE across this assembly (bir2cir keys top-level
// suspend funs and suspend member cold entries by simple name). The former `main` + golden -> one @TestAttribute
// method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import System.Threading.Tasks.Task
import dotkt.support.blockOn

interface CrossinlineObjectSuspendSink {
    suspend fun accept(value: Int): Boolean
}

suspend fun crossinlineObjectIsBelow(v: Int, limit: Int): Boolean = v < limit

suspend fun crossinlineObjectDriveSink(sink: CrossinlineObjectSuspendSink, v: Int): Boolean = sink.accept(v)

inline fun crossinlineObjectMakeAndDrive(v: Int, crossinline predicate: suspend (Int) -> Boolean): Boolean {
    val sink = object : CrossinlineObjectSuspendSink {
        override suspend fun accept(value: Int): Boolean = predicate(value)
    }
    return blockOn { crossinlineObjectDriveSink(sink, v) }
}

suspend inline fun crossinlineObjectCaptureAndDrive(
    crossinline predicate: suspend (Int) -> Boolean
): Boolean {
    val sink = object : CrossinlineObjectSuspendSink {
        override suspend fun accept(value: Int): Boolean = predicate(value)
    }
    return sink.accept(1)
}

fun crossinlineObjectCaptureWrap(
    transform: suspend (Int) -> Boolean
): suspend () -> Boolean = {
    crossinlineObjectCaptureAndDrive { value -> transform(value) }
}

suspend inline fun crossinlineObjectCaptureForward(
    crossinline predicate: suspend (Int) -> Boolean
): Boolean = crossinlineObjectCaptureAndDrive { value -> predicate(value) }

fun crossinlineObjectTwoLevelWrap(
    transform: suspend (Int) -> Boolean
): suspend () -> Boolean = {
    crossinlineObjectCaptureForward { value -> transform(value) }
}

fun crossinlineObjectMutableWrap(): suspend () -> Int {
    var state = 1
    val accepted = crossinlineObjectCaptureWrap { value ->
        state += value
        Task.Delay(1).await()
        state += value * 2
        true
    }
    return {
        state += 10
        accepted()
        state
    }
}

class CrossinlineSuspendObjectTests {
    @TestAttribute
    fun crossinlineSuspendIntoObjectLiteral() {
        assertEquals(true, crossinlineObjectMakeAndDrive(41) { crossinlineObjectIsBelow(it, 42) })   // True
        assertEquals(false, crossinlineObjectMakeAndDrive(42) { crossinlineObjectIsBelow(it, 42) })  // False
        assertEquals(true, crossinlineObjectMakeAndDrive(0) { crossinlineObjectIsBelow(it, 42) })    // True
        assertEquals(true, blockOn { crossinlineObjectCaptureWrap { it == 1 }() })
        assertEquals(true, blockOn { crossinlineObjectTwoLevelWrap { it == 1 }() })
        assertEquals(14, blockOn { crossinlineObjectMutableWrap()() })
    }
}
