// CorB batch — il-inlsuspenddefault: the #43 Batch A x B integration seam. A crossinline SUSPEND carrier (materialized
// §4.4ii) whose body nests a MEMBER-inline call that OMITS a lambda-typed default (filled via the #34 member-inline
// default carriage -> a `newDelegate` inside the carrier). All top-level decls carry the `idef` case token under the
// shared `corB`/`CorB` prefix so their simple names are UNIQUE across this assembly (bir2cir's cold-core suspend
// lowering keys top-level suspend funs by simple name). The former `main` + golden -> one @TestAttribute method (1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

suspend fun corBIdefAddA(a: Int, b: Int): Int = a + b

class CorBIdefChooser(val base: Int) {
    inline fun pick(cond: Boolean, primary: () -> Int, fallback: () -> Int = { -1 }): Int =
        if (cond) primary() else fallback()
}

inline fun corBIdefWrap(x: Int, crossinline t: suspend (Int) -> Int): Int = blockOn { t(x) }

class CorBInlsuspenddefaultTests {
    @TestAttribute
    fun inlsuspenddefault_nestedMemberInlineOmittedLambdaDefault() {
        val c = CorBIdefChooser(3)
        assertEquals(19, corBIdefWrap(20) { corBIdefAddA(it, c.pick(false, { 5 })) })   // addA(20, -1) = 19
        assertEquals(15, corBIdefWrap(10) { corBIdefAddA(it, c.pick(true, { 5 })) })    // 15
        assertEquals(-1, corBIdefWrap(0) { corBIdefAddA(it, c.pick(false, { 5 })) })    // addA(0, -1) = -1
    }
}
