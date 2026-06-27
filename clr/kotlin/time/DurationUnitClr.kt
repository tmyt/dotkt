// Step-1 CLR stub mirroring the JVM `actual` declarations for this source set.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.time

@SinceKotlin("1.6")
public actual enum class DurationUnit {
    /**
     * Time unit representing one nanosecond, which is 1/1000 of a microsecond.
     */
    NANOSECONDS,
    /**
     * Time unit representing one microsecond, which is 1/1000 of a millisecond.
     */
    MICROSECONDS,
    /**
     * Time unit representing one millisecond, which is 1/1000 of a second.
     */
    MILLISECONDS,
    /**
     * Time unit representing one second.
     */
    SECONDS,
    /**
     * Time unit representing one minute.
     */
    MINUTES,
    /**
     * Time unit representing one hour.
     */
    HOURS,
    /**
     * Time unit representing one day, which is always equal to 24 hours.
     */
    DAYS;
}

@SinceKotlin("1.3")
internal actual fun convertDurationUnit(value: Double, sourceUnit: DurationUnit, targetUnit: DurationUnit): Double = TODO("clr binding should be implemented")

@SinceKotlin("1.5")
internal actual fun convertDurationUnitOverflow(value: Long, sourceUnit: DurationUnit, targetUnit: DurationUnit): Long = TODO("clr binding should be implemented")

@SinceKotlin("1.5")
internal actual fun convertDurationUnit(value: Long, sourceUnit: DurationUnit, targetUnit: DurationUnit): Long = TODO("clr binding should be implemented")
