/*
 * Copyright 2010-2025 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// Demonstrations of the implicit-byref binding for BCL `out` parameters (the `ref` form is exercised by the atomics'
// Interlocked in builtins/Atomics.kt). Kotlin has no `out`/`ref` syntax: a helper declares its byref parameter as
// `ClrRef<T>` (-> `T&`) and the call site passes `byref(local)`, whose address (`ldloca`) flows into the BCL `out`
// parameter. bir2cir reads these @ClrIntrinsic labels from the ref.dll and substitutes the call to the BCL static.

package kotlin.clr

@kotlin.clr.ClrIntrinsic("System.Int32.TryParse")
internal fun tryParseInt32(s: String, result: ClrRef<Int>): Boolean = TODO("clr binding should be implemented")

/** `int.TryParse(s, out result)` — returns the parsed value, or [fallback] when [s] is not a valid Int. */
public fun parseIntOrElse(s: String, fallback: Int): Int {
    var r = 0
    return if (tryParseInt32(s, byref(r))) r else fallback
}

@kotlin.clr.ClrIntrinsic("System.Math.DivRem")
internal fun mathDivRemInt(a: Int, b: Int, remainder: ClrRef<Int>): Int = TODO("clr binding should be implemented")

/** `Math.DivRem(a, b, out rem)` — the quotient is the return value, the remainder is written through the `out` param;
 *  packed here as `quotient * 1000 + remainder` so the byref write-back is observable in a single Int. */
public fun divRemPacked(a: Int, b: Int): Int {
    var rem = 0
    val q = mathDivRemInt(a, b, byref(rem))
    return q * 1000 + rem
}
