// CorB batch — il-inlsuspendobj: the FORMER SILENT-MISCOMPILE cell — a `crossinline` SUSPEND lambda captured by an
// `object :` literal inside an inline body, materialized §4.4ii into a real newSuspendLambda VALUE. All decls carry
// the `iobj`/`CorBIobj` case token so their simple names are UNIQUE across this assembly (bir2cir keys top-level
// suspend funs and suspend member cold entries by simple name). The former `main` + golden -> one @TestAttribute
// method (values 1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

interface CorBIobjSuspendSink {
    suspend fun accept(value: Int): Boolean
}

suspend fun corBIobjIsBelow(v: Int, limit: Int): Boolean = v < limit

suspend fun corBIobjDriveSink(sink: CorBIobjSuspendSink, v: Int): Boolean = sink.accept(v)

inline fun corBIobjMakeAndDrive(v: Int, crossinline predicate: suspend (Int) -> Boolean): Boolean {
    val sink = object : CorBIobjSuspendSink {
        override suspend fun accept(value: Int): Boolean = predicate(value)
    }
    return blockOn { corBIobjDriveSink(sink, v) }
}

class CrossinlineSuspendObjectTests {
    @TestAttribute
    fun crossinlineSuspendIntoObjectLiteral() {
        assertEquals(true, corBIobjMakeAndDrive(41) { corBIobjIsBelow(it, 42) })   // True
        assertEquals(false, corBIobjMakeAndDrive(42) { corBIobjIsBelow(it, 42) })  // False
        assertEquals(true, corBIobjMakeAndDrive(0) { corBIobjIsBelow(it, 42) })    // True
    }
}
