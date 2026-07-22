// Kotlin-producer roundtrip for il-tloverload: facadegen restores same-name top-level overloads from two real DotKt
// file facades (N5.UtilsKt and N5.HelpersKt), routing their shared CallableId(N5, "foo") by value-parameter arity.
import N5.*
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

class TopLevelOverloadTests {
    @TestAttribute
    fun samePackageTopLevelOverloadsFromDifferentKotlinFiles() {
        assertEquals(100, foo())
        assertEquals(42, foo(41))
    }
}
