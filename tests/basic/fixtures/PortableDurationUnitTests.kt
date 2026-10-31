import NUnit.Framework.TestAttribute
import kotlin.enums.enumEntries
import kotlin.time.Duration
import kotlin.time.DurationUnit
import kotlin.time.TimeSource
import kotlin.time.toDuration

private fun durationUnitScale(unit: DurationUnit): Long = when (unit) {
    DurationUnit.NANOSECONDS -> 1L
    DurationUnit.MICROSECONDS -> 1_000L
    DurationUnit.MILLISECONDS -> 1_000_000L
    DurationUnit.SECONDS -> 1_000_000_000L
    DurationUnit.MINUTES -> 60_000_000_000L
    DurationUnit.HOURS -> 3_600_000_000_000L
    DurationUnit.DAYS -> 86_400_000_000_000L
}

private fun <E : Enum<E>> durationEnumOrdinal(value: E): Int = value.ordinal

class PortableDurationUnitTests {
    @TestAttribute fun enumValuesAndGenericOperationsKeepDeclarationOrder() {
        val names = listOf("NANOSECONDS", "MICROSECONDS", "MILLISECONDS", "SECONDS", "MINUTES", "HOURS", "DAYS")
        val values = enumValues<DurationUnit>()
        check(values.size == 7) { "values count: ${values.size}" }
        val actualNames = DurationUnit.entries.map { it.name }
        check(actualNames == names) { actualNames.toString() }
        check(enumEntries<DurationUnit>() === DurationUnit.entries) { "entries cache identity" }
        val name = DurationUnit::name
        val compare = DurationUnit::compareTo
        for ((index, unit) in values.withIndex()) {
            check(unit.ordinal == index) { "direct ordinal at $index: ${unit.ordinal}" }
            val genericOrdinal = durationEnumOrdinal(unit)
            check(genericOrdinal == index) { "generic ordinal at $index: $genericOrdinal" }
            check(name(unit) == names[index]) { "name reference at $index: ${name(unit)}" }
            check(unit.toString() == names[index]) { "toString at $index: $unit" }
            check(DurationUnit.valueOf(names[index]) === unit) { "valueOf identity at $index" }
            check(enumValueOf<DurationUnit>(names[index]) === unit) { "generic valueOf identity at $index" }
            val ordering = compare(unit, DurationUnit.NANOSECONDS)
            check(if (index == 0) ordering == 0 else ordering > 0) { "comparison at $index: $ordering" }
        }
    }

    @TestAttribute fun allUnitPairsPreserveWholeValueConversions() {
        for (source in DurationUnit.entries) {
            for (target in DurationUnit.entries) {
                val sourceScale = durationUnitScale(source)
                val targetScale = durationUnitScale(target)
                for (value in listOf(-1001L, -1L, 0L, 1L, 1001L)) {
                    val expected = if (sourceScale > targetScale)
                        value * (sourceScale / targetScale)
                    else value / (targetScale / sourceScale)
                    check(value.toDuration(source).toLong(target) == expected)
                }
            }
        }
    }

    @TestAttribute fun infinityZeroAndFractionalConversionsRetainTheirContracts() {
        for (unit in DurationUnit.entries) {
            check(Duration.ZERO.toLong(unit) == 0L)
            check(Duration.INFINITE.toLong(unit) == Long.MAX_VALUE)
            check((-Duration.INFINITE).toLong(unit) == Long.MIN_VALUE)
            check(Duration.INFINITE.toDouble(unit) == Double.POSITIVE_INFINITY)
        }
        check(Long.MAX_VALUE.toDuration(DurationUnit.DAYS).isInfinite())
        check(Long.MIN_VALUE.toDuration(DurationUnit.DAYS).isInfinite())
        check(1.5.toDuration(DurationUnit.SECONDS).toDouble(DurationUnit.MILLISECONDS) == 1500.0)
        check((-1.5).toDuration(DurationUnit.SECONDS).inWholeNanoseconds == -1_500_000_000L)
    }

    @TestAttribute fun parsingAndFormattingUseTheUnchangedCommonBodies() {
        val duration = Duration.parse("1h 2m 3.5s")
        check(duration == 3723.5.toDuration(DurationUnit.SECONDS))
        check(duration.toIsoString() == "PT1H2M3.500S")
        check(Duration.parseIsoString("-PT1.25S").inWholeNanoseconds == -1_250_000_000L)
        check(Duration.parse("0.000000001s").toLong(DurationUnit.NANOSECONDS) == 1L)
    }

    @TestAttribute fun monotonicMarksKeepTheirUnitsAndOrdering() {
        val mark = TimeSource.Monotonic.markNow()
        check(mark.elapsedNow() >= Duration.ZERO)
        val later = mark + 1L.toDuration(DurationUnit.SECONDS)
        check(later - mark == 1L.toDuration(DurationUnit.SECONDS))
        check(later > mark)
    }
}
