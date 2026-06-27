// Step-1 CLR stub mirroring the JVM `actual` declarations for this source set.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.time

import kotlin.time.TimeSource.Monotonic.ValueTimeMark

@SinceKotlin("1.3")
internal actual object MonotonicTimeSource : TimeSource.WithComparableMarks {
    actual override fun markNow(): ValueTimeMark = TODO("clr binding should be implemented")
    actual fun elapsedFrom(timeMark: ValueTimeMark): Duration = TODO("clr binding should be implemented")
    actual fun differenceBetween(one: ValueTimeMark, another: ValueTimeMark): Duration = TODO("clr binding should be implemented")
    actual fun adjustReading(timeMark: ValueTimeMark, duration: Duration): ValueTimeMark = TODO("clr binding should be implemented")
}

@Suppress("ACTUAL_WITHOUT_EXPECT") // visibility
internal actual class ValueTimeMarkReading
