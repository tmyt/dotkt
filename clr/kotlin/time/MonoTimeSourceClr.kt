// Step-1 CLR stub mirroring the JVM `actual` declarations for this source set.
// Bodies are bound through tiny `@ClrIntrinsic` BCL primitives, wrapped by the pure-Kotlin logic
// copied from the JVM actual (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.time

import kotlin.time.TimeSource.Monotonic.ValueTimeMark

// The reading carried by a ValueTimeMark is a plain nanosecond counter (Long), exactly as on the JVM.
@Suppress("ACTUAL_WITHOUT_EXPECT") // visibility
internal actual typealias ValueTimeMarkReading = Long

// --- @Clr BCL primitives ----------------------------------------------------------------------
// The monotonic source is `System.Diagnostics.Stopwatch`: GetTimestamp() yields a high-resolution
// monotonic counter, and GetElapsedTime(start) converts a span of counter ticks to a TimeSpan
// (sidestepping the platform-dependent Stopwatch.Frequency, which is a static FIELD and therefore
// not bindable through the @Clr static-method mechanism).

@kotlin.clr.ClrIntrinsic("System.TimeSpan")
private class ClrTimeSpan {
    @kotlin.clr.ClrIntrinsic("get_Ticks")
    fun ticks(): Long = TODO("clr binding should be implemented")
}

@kotlin.clr.ClrIntrinsic("System.Diagnostics.Stopwatch.GetTimestamp")
private fun clrTimestamp(): Long = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Diagnostics.Stopwatch.GetElapsedTime")
private fun clrElapsedSince(startingTimestamp: Long): ClrTimeSpan = TODO("clr binding should be implemented")

@SinceKotlin("1.3")
internal actual object MonotonicTimeSource : TimeSource.WithComparableMarks {
    private val zero: Long = clrTimestamp()
    // Nanoseconds elapsed since `zero`. TimeSpan.Ticks are 100-nanosecond units, so scale by 100.
    private fun read(): Long = clrElapsedSince(zero).ticks() * 100
    override fun toString(): String = "TimeSource(Stopwatch.GetTimestamp())"

    actual override fun markNow(): ValueTimeMark = ValueTimeMark(read())
    actual fun elapsedFrom(timeMark: ValueTimeMark): Duration =
        saturatingDiff(read(), timeMark.reading, DurationUnit.NANOSECONDS)

    actual fun differenceBetween(one: ValueTimeMark, another: ValueTimeMark): Duration =
        saturatingOriginsDiff(one.reading, another.reading, DurationUnit.NANOSECONDS)

    actual fun adjustReading(timeMark: ValueTimeMark, duration: Duration): ValueTimeMark =
        ValueTimeMark(saturatingAdd(timeMark.reading, DurationUnit.NANOSECONDS, duration))
}
