@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.random

import kotlin.internal.InlineOnly

// CLR entropy seed: a high-resolution, ever-advancing monotonic counter (System.Diagnostics.Stopwatch.GetTimestamp).
// Distinct calls observe distinct timestamps, so each default Random instance is seeded differently.
@kotlin.clr.ClrIntrinsic("System.Diagnostics.Stopwatch.GetTimestamp")
internal fun clrEntropyTimestamp(): Long = TODO("clr binding should be implemented")

@InlineOnly
internal actual inline fun defaultPlatformRandom(): Random =
    Random(clrEntropyTimestamp())

internal actual fun doubleFromParts(hi26: Int, low27: Int): Double =
    (hi26.toLong().shl(27) + low27) / (1L shl 53).toDouble()
