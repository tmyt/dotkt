// feature fixture — il-inlsuspendobj: the FORMER SILENT-MISCOMPILE cell — a `crossinline` SUSPEND lambda captured by an
// `object :` literal inside an inline body, materialized §4.4ii into a real newSuspendLambda VALUE. All decls carry
// the descriptive `crossinlineObject`/`CrossinlineObject` stem so their simple names are UNIQUE across this assembly (bir2cir keys top-level
// suspend funs and suspend member cold entries by simple name). The former `main` + golden -> one @TestAttribute
// method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
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

class CrossinlineSuspendObjectTests {
    @TestAttribute
    fun crossinlineSuspendIntoObjectLiteral() {
        assertEquals(true, crossinlineObjectMakeAndDrive(41) { crossinlineObjectIsBelow(it, 42) })   // True
        assertEquals(false, crossinlineObjectMakeAndDrive(42) { crossinlineObjectIsBelow(it, 42) })  // False
        assertEquals(true, crossinlineObjectMakeAndDrive(0) { crossinlineObjectIsBelow(it, 42) })    // True
    }
}
