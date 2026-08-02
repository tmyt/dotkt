// feature fixture — il-inlsuspendobj: the FORMER SILENT-MISCOMPILE cell — a `crossinline` SUSPEND lambda captured by an
// `object :` literal inside an inline body, materialized §4.4ii into a real newSuspendLambda VALUE. All decls carry
// the `iobj`/`CrossinlineObjectIobj` case token so their simple names are UNIQUE across this assembly (bir2cir keys top-level
// suspend funs and suspend member cold entries by simple name). The former `main` + golden -> one @TestAttribute
// method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

interface CrossinlineObjectIobjSuspendSink {
    suspend fun accept(value: Int): Boolean
}

suspend fun crossinlineObjectIobjIsBelow(v: Int, limit: Int): Boolean = v < limit

suspend fun crossinlineObjectIobjDriveSink(sink: CrossinlineObjectIobjSuspendSink, v: Int): Boolean = sink.accept(v)

inline fun crossinlineObjectIobjMakeAndDrive(v: Int, crossinline predicate: suspend (Int) -> Boolean): Boolean {
    val sink = object : CrossinlineObjectIobjSuspendSink {
        override suspend fun accept(value: Int): Boolean = predicate(value)
    }
    return blockOn { crossinlineObjectIobjDriveSink(sink, v) }
}

class CrossinlineSuspendObjectTests {
    @TestAttribute
    fun crossinlineSuspendIntoObjectLiteral() {
        assertEquals(true, crossinlineObjectIobjMakeAndDrive(41) { crossinlineObjectIobjIsBelow(it, 42) })   // True
        assertEquals(false, crossinlineObjectIobjMakeAndDrive(42) { crossinlineObjectIobjIsBelow(it, 42) })  // False
        assertEquals(true, crossinlineObjectIobjMakeAndDrive(0) { crossinlineObjectIobjIsBelow(it, 42) })    // True
    }
}
