// #199 regression (delegate part B) — the same-simple-name twin of `xpkg199da.xdFoo`, in package `xpkg199db`,
// returning a DIFFERENT value so a mis-dispatch (both delegates binding to the first package) is observable.
package xpkg199db

fun xdFoo(): Int = 2

val bDeleg: () -> Int = ::xdFoo
