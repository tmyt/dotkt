// #199 regression (part B) — the same-simple-name twin of `xpkg199a.xFoo`, in package `xpkg199b`, returning a
// DIFFERENT value so a mis-dispatch (both binding to the first package) is observable.
package xpkg199b

fun xFoo(): Int = 2
