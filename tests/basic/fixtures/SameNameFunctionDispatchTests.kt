// #199 regression — two top-level funs with the SAME simple name (`xFoo`) in DIFFERENT packages
// (xpkg199a / xpkg199b), each called via an aliased import, asserting BOTH dispatch to their OWN package's
// body (1 vs 2). Guards the #199 root for the NON-suspend top-level call path (Design B): the cross-package
// top-level `callStatic` keeps `owner:null` (the load-bearing substitution axis) and carries a `calleeOwner`
// file-class DISPATCH hint, which ilemit resolves the owner-null static through — rather than a global
// first-match. (Suspend analogue: SameNameAcrossPackagesTests in tests/coroutines.)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import xpkg199a.xFoo as aFoo
import xpkg199b.xFoo as bFoo

class SameNameFunctionDispatchTests {
    @TestAttribute
    fun bothPackagesCallTheirOwnFunction() {
        assertEquals(1, aFoo())
        assertEquals(2, bFoo())
    }
}
