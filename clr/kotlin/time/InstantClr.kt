// Step-1 CLR stub mirroring the JVM `actual` declarations for this source set.
// Bodies are bound through tiny `@ClrIntrinsic` BCL primitives, wrapped by the pure-Kotlin logic
// copied from the JVM actual (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.time

// --- @Clr BCL primitives ----------------------------------------------------------------------
// Wall-clock "now" comes from System.DateTimeOffset.UtcNow (a static property getter returning the
// DateTimeOffset struct), then ToUnixTimeMilliseconds() on that value.

@kotlin.clr.ClrIntrinsic("System.DateTimeOffset")
private class ClrDateTimeOffset {
    @kotlin.clr.ClrIntrinsic("ToUnixTimeMilliseconds")
    fun toUnixTimeMilliseconds(): Long = TODO("clr binding should be implemented")
}

@kotlin.clr.ClrIntrinsic("System.DateTimeOffset.get_UtcNow")
private fun clrUtcNow(): ClrDateTimeOffset = TODO("clr binding should be implemented")

@ExperimentalTime
internal actual fun systemClockNow(): Instant =
    Instant.fromEpochMilliseconds(clrUtcNow().toUnixTimeMilliseconds())

@ExperimentalTime
internal actual fun serializedInstant(instant: Instant): Any =
    throw UnsupportedOperationException("Serialization is supported only on the JVM")
