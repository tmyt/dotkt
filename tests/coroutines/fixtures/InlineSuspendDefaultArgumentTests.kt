// feature fixture — il-inlsuspenddefault: the #43 Batch A x B integration seam. A crossinline SUSPEND carrier (materialized
// §4.4ii) whose body nests a MEMBER-inline call that OMITS a lambda-typed default (filled via the #34 member-inline
// default carriage -> a `newDelegate` inside the carrier). All top-level declarations use the descriptive
// `inlineSuspendDefault`/`InlineSuspendDefault` stem so their simple names are UNIQUE across this assembly (bir2cir's cold-core suspend
// lowering keys top-level suspend funs by simple name). The former `main` + golden -> one @TestAttribute method (1:1).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn

suspend fun inlineSuspendDefaultAdd(a: Int, b: Int): Int = a + b

class InlineSuspendDefaultChooser(val base: Int) {
    inline fun pick(cond: Boolean, primary: () -> Int, fallback: () -> Int = { -1 }): Int =
        if (cond) primary() else fallback()
}

inline fun inlineSuspendDefaultWrap(x: Int, crossinline t: suspend (Int) -> Int): Int = blockOn { t(x) }

class InlineSuspendDefaultArgumentTests {
    @TestAttribute
    fun nestedMemberInlineOmittedLambdaDefault() {
        val c = InlineSuspendDefaultChooser(3)
        assertEquals(19, inlineSuspendDefaultWrap(20) { inlineSuspendDefaultAdd(it, c.pick(false, { 5 })) })   // add(20, -1) = 19
        assertEquals(15, inlineSuspendDefaultWrap(10) { inlineSuspendDefaultAdd(it, c.pick(true, { 5 })) })    // 15
        assertEquals(-1, inlineSuspendDefaultWrap(0) { inlineSuspendDefaultAdd(it, c.pick(false, { 5 })) })    // add(0, -1) = -1
    }
}
