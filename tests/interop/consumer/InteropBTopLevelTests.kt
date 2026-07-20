// C#-producer roundtrip consumer battery B — TOP-LEVEL function restoration from .NET file facades.
//   tloverload <- il-tloverload  same-name same-package top-level overloads restored from DIFFERENT .NET file
//                                facades (N5.UtilsKt.foo() vs N5.HelpersKt.foo(Int)); they share
//                                CallableId(N5, "foo") and the overload-aware key routes each by value-param arity.
// Own file to isolate the `import N5.*` wildcard that brings the restored top-level `foo` overloads into scope.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import N5.*

class InteropBTopLevelTests {
    @TestAttribute
    fun tloverload() {
        assertEquals(100, foo())    // 100 — N5.UtilsKt.foo()
        assertEquals(42, foo(41))   // 42 — N5.HelpersKt.foo(41)
    }
}
