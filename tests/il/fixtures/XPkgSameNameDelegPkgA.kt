// #199 regression (delegate part A) — a top-level fun `xdFoo` in package `xpkg199da`, sharing its SIMPLE name
// with `xpkg199db.xdFoo`, and a top-level val holding a `::xdFoo` function-reference DELEGATE resolved in THIS
// package. Pre-fix kotc emitted the bare-name `newDelegate method:xdFoo` (discarding the FIR-resolved callee
// file-class) and ilemit bound it by global first-match FindStatic -> BOTH delegates dispatched to the first
// package's body. The function-REFERENCE analogue of the XPkgSameNameFun (direct-call) fixture.
package xpkg199da

fun xdFoo(): Int = 1

val aDeleg: () -> Int = ::xdFoo
