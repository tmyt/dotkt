// #199 regression — two top-level suspend funs with the SAME simple name (`pkgFoo`) in DIFFERENT packages
// (col199a / col199b), both DRIVEN via the shared cold-core blockOn harness, asserting BOTH run and return their
// OWN package's value (11 vs "b!"). Guards against the umbrella #199 root: resolution/indexing keyed by simple
// name instead of owner-qualified FQN.
//
// NOTE (coordinator): this fixture asserts END-TO-END dispatch, which requires ALL THREE #199 manifestations
// fixed together — (1) bir2cir declaration-side FunKey dedup [DONE, this branch], (2) kotc emitting the callee's
// file-class owner instead of `owner:null` for a cross-package top-level call [BirEmitterCalls.kt], and
// (3) ilemit resolving an owner-qualified `callStatic` rather than a global first-match `FindStatic`. Land this
// fixture WITH (2)+(3); on the bir2cir fix alone it still mis-dispatches (both return the first package's value).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import dotkt.support.blockOn
import col199a.pkgFoo as aFoo
import col199b.pkgFoo as bFoo

class SameNameAcrossPackagesTests {
    @TestAttribute
    fun bothPackagesRunTheirOwnBody() {
        assertEquals(11, blockOn { aFoo() })     // col199a.pkgFoo -> col199a.pkgLeaf()+1
        assertEquals("b!", blockOn { bFoo() })   // col199b.pkgFoo -> col199b.pkgLeaf()+"!"
    }
}
