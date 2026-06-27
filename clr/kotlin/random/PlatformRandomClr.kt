@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.random

import kotlin.internal.InlineOnly

@InlineOnly
internal actual inline fun defaultPlatformRandom(): Random =
    TODO("clr binding should be implemented")

internal actual fun doubleFromParts(hi26: Int, low27: Int): Double =
    TODO("clr binding should be implemented")
