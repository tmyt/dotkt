package roundtrip.portabledurationunit

import kotlin.enums.EnumEntries
import kotlin.time.Duration
import kotlin.time.DurationUnit
import kotlin.time.toDuration

fun importedUnit(value: DurationUnit): DurationUnit = value
fun importedDurationUnits(): EnumEntries<DurationUnit> = DurationUnit.entries
fun importedDuration(value: Long, unit: DurationUnit): Duration = value.toDuration(unit)

enum class ImportedDurationScale(val amount: Int) {
    SMALL(1) { override fun label(): String = "small" }, LARGE(2);

    open fun label(): String = "large"
}
