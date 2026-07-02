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

// Each unit's size expressed in nanoseconds. Conversions are exact integer
// ratios between any two units, so we scale by the source/target ratio. This
// mirrors Kotlin's non-JVM (JS/Native) `convertDurationUnit` implementation
// (the JVM uses java.util.concurrent.TimeUnit, which CLR has no equivalent of).
private fun DurationUnit.scaleInNanos(): Long = when (this) {
    DurationUnit.NANOSECONDS -> 1L
    DurationUnit.MICROSECONDS -> 1_000L
    DurationUnit.MILLISECONDS -> 1_000_000L
    DurationUnit.SECONDS -> 1_000_000_000L
    DurationUnit.MINUTES -> 60_000_000_000L
    DurationUnit.HOURS -> 3_600_000_000_000L
    DurationUnit.DAYS -> 86_400_000_000_000L
    else -> error("Unknown unit: $this")
}

@SinceKotlin("1.3")
internal actual fun convertDurationUnit(value: Double, sourceUnit: DurationUnit, targetUnit: DurationUnit): Double {
    val sourceScale = sourceUnit.scaleInNanos()
    val targetScale = targetUnit.scaleInNanos()
    return when {
        sourceScale > targetScale -> value * (sourceScale / targetScale)
        sourceScale < targetScale -> value / (targetScale / sourceScale)
        else -> value
    }
}

@SinceKotlin("1.5")
internal actual fun convertDurationUnitOverflow(value: Long, sourceUnit: DurationUnit, targetUnit: DurationUnit): Long {
    val sourceScale = sourceUnit.scaleInNanos()
    val targetScale = targetUnit.scaleInNanos()
    return when {
        sourceScale > targetScale -> value * (sourceScale / targetScale)
        sourceScale < targetScale -> value / (targetScale / sourceScale)
        else -> value
    }
}

@SinceKotlin("1.5")
internal actual fun convertDurationUnit(value: Long, sourceUnit: DurationUnit, targetUnit: DurationUnit): Long {
    val sourceScale = sourceUnit.scaleInNanos()
    val targetScale = targetUnit.scaleInNanos()
    return when {
        sourceScale > targetScale -> {
            val scale = sourceScale / targetScale
            when {
                value > Long.MAX_VALUE / scale -> Long.MAX_VALUE
                value < Long.MIN_VALUE / scale -> Long.MIN_VALUE
                else -> value * scale
            }
        }
        sourceScale < targetScale -> value / (targetScale / sourceScale)
        else -> value
    }
}
