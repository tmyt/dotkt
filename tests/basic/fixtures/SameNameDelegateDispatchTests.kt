// #199 regression — two top-level funs with the SAME simple name (`xdFoo`) in DIFFERENT packages
// (xpkg199da / xpkg199db), each referenced via a `::xdFoo` function-reference DELEGATE (a top-level val in its
// own package file), asserting BOTH delegates dispatch to their OWN package's body (1 vs 2). Guards the #199
// root for the function-REFERENCE path (Design B extended to newDelegate): kotc stamps the bare-name
// `newDelegate method:xdFoo` with a `calleeOwner` file-class DISPATCH hint, which ilemit binds the owner-less
// delegate target through — rather than a global first-match FindStatic. (Direct-call analogue:
// SameNameFunctionDispatchTests; suspend analogue: SameNameAcrossPackagesTests in tests/coroutines.)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import xpkg199da.aDeleg
import xpkg199db.bDeleg

class SameNameDelegateDispatchTests {
    @TestAttribute
    fun bothDelegatesCallTheirOwnFunction() {
        assertEquals(1, aDeleg())
        assertEquals(2, bDeleg())
    }
}
