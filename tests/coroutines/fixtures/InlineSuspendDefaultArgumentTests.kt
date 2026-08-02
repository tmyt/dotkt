// feature fixture — il-inlsuspenddefault: the #43 Batch A x B integration seam. A crossinline SUSPEND carrier (materialized
// §4.4ii) whose body nests a MEMBER-inline call that OMITS a lambda-typed default (filled via the #34 member-inline
// default carriage -> a `newDelegate` inside the carrier). All top-level decls carry the `idef` case token under the
// shared `inlineSuspendDefault`/`InlineSuspendDefault` prefix so their simple names are UNIQUE across this assembly (bir2cir's cold-core suspend
// lowering keys top-level suspend funs by simple name). The former `main` + golden -> one @TestAttribute method (1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

suspend fun inlineSuspendDefaultIdefAddA(a: Int, b: Int): Int = a + b

class InlineSuspendDefaultIdefChooser(val base: Int) {
    inline fun pick(cond: Boolean, primary: () -> Int, fallback: () -> Int = { -1 }): Int =
        if (cond) primary() else fallback()
}

inline fun inlineSuspendDefaultIdefWrap(x: Int, crossinline t: suspend (Int) -> Int): Int = blockOn { t(x) }

class InlineSuspendDefaultArgumentTests {
    @TestAttribute
    fun nestedMemberInlineOmittedLambdaDefault() {
        val c = InlineSuspendDefaultIdefChooser(3)
        assertEquals(19, inlineSuspendDefaultIdefWrap(20) { inlineSuspendDefaultIdefAddA(it, c.pick(false, { 5 })) })   // addA(20, -1) = 19
        assertEquals(15, inlineSuspendDefaultIdefWrap(10) { inlineSuspendDefaultIdefAddA(it, c.pick(true, { 5 })) })    // 15
        assertEquals(-1, inlineSuspendDefaultIdefWrap(0) { inlineSuspendDefaultIdefAddA(it, c.pick(false, { 5 })) })    // addA(0, -1) = -1
    }
}
