// Step-1 CLR stub mirroring the JVM `actual` declarations for this source set.
// Bodies are bound through tiny `@ClrIntrinsic` BCL primitives, wrapped by the pure-Kotlin logic
// copied from the JVM/JS actual (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.time

// Matches the JS actual: duration self-assertions are always enabled in this build.
internal actual val durationAssertionsEnabled: Boolean get() = true

// --- @Clr BCL primitives ----------------------------------------------------------------------
// Fixed-point formatting with a '.' decimal separator: System.Double.ToString("F<decimals>",
// CultureInfo.InvariantCulture). The "F" standard format already rounds to `decimals` places.

@kotlin.clr.ClrTypeAlias("System.Globalization.CultureInfo")
private class ClrCultureInfo

@kotlin.clr.ClrIntrinsic("System.Globalization.CultureInfo.get_InvariantCulture")
private fun clrInvariantCulture(): ClrCultureInfo = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("ToString")
private fun Double.clrFormat(format: String, provider: ClrCultureInfo): String = TODO("clr binding should be implemented")

internal actual fun formatToExactDecimals(value: Double, decimals: Int): String =
    value.clrFormat("F" + decimals, clrInvariantCulture())
