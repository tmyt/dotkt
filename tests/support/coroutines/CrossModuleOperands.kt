// CROSS-MODULE NON-SUSPEND callees for the operand batteries in tests/coroutines.
//
// They live here, in a separate assembly, for the same reason CrossModuleSuspend.kt's do: a call to a REFERENCED
// callee is emitted by kotc as a plain call by the .NET owner's IDENTITY, and bir2cir's NetInteropBinding then
// reshapes it into the `clr*` vocabulary — `clrStatic` for a plain top-level fun, `clrGenericStatic` for a generic
// one. That reshape is where a node's result-type stamps can be dropped (#304), and a same-module fixture never
// reaches it: a same-module top-level call keeps its Kotlin `callStatic` shape all the way down.
//
// These are deliberately NOT suspend: what the fixtures pin is the reshaped node used as an ORDINARY operand
// standing left of somebody else's suspension, which is the composition that has to be typed and spilled.
package dotkt.support

/** Cross-module GENERIC top-level fun — a `clrGenericStatic` at every call site outside this assembly. */
public fun <T> corStampPack(a: T, b: Int): String = "" + a + "/" + b

/** Cross-module NON-generic top-level fun — a `clrStatic`, the sibling shape. */
public fun corStampPlain(a: Int, b: Int): String = "" + a + "/" + b
