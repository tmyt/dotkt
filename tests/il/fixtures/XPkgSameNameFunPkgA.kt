// #199 regression (part A) — a top-level fun `xFoo` in package `xpkg199a`, sharing its SIMPLE name with
// `xpkg199b.xFoo`. Pre-fix kotc emitted the cross-package top-level `callStatic` with `owner:null` (discarding
// the FIR-resolved callee file-class) and ilemit resolved it by global first-match -> BOTH calls dispatched to
// the first package's body (a.xFoo). The non-suspend analogue of the coroutine SameNameAcrossPackages fixture.
// (Re-homed from cases/il-xpkgfun/ into the tests/il NUnit lane; bash cases are frozen per the migration.)
package xpkg199a

fun xFoo(): Int = 1
