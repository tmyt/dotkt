package roundtriptests.portabledurationunit

import NUnit.Framework.TestAttribute
import kotlin.enums.enumEntries
import kotlin.time.DurationUnit
import roundtrip.portabledurationunit.*

private fun <E : Enum<E>> importedEnumOrdinal(value: E): Int = value.ordinal

class PortableDurationUnitTests {
    @TestAttribute fun importedUnitsKeepTheirEnumIdentityAndCachedEntries() {
        check(importedDurationUnits() === DurationUnit.entries)
        check(importedDurationUnits() === enumEntries<DurationUnit>())
        for ((index, unit) in importedDurationUnits().withIndex()) {
            check(importedUnit(unit) === unit)
            check(importedUnit(unit).ordinal == index)
            check(importedEnumOrdinal(importedUnit(unit)) == index)
            check(DurationUnit.valueOf(importedUnit(unit).name) === unit)
        }
    }

    @TestAttribute fun importedDurationValuesKeepConcreteUnitSignatures() {
        for (unit in DurationUnit.entries) {
            val actual = importedDuration(2L, unit).toLong(unit)
            check(actual == 2L) { "$unit: $actual instead of 2" }
        }
        val iso = importedDuration(-1250L, DurationUnit.MILLISECONDS).toIsoString()
        check(iso == "-PT1.250S") { iso }
    }

    @TestAttribute fun importedRichEnumPropertiesUseProducerFieldFacts() {
        val value = ImportedDurationScale.LARGE
        val name = ImportedDurationScale::name
        val ordinal = ImportedDurationScale::ordinal
        check(value.toString() == "LARGE")
        check(name(value) == "LARGE" && ordinal(value) == 1)
        check(value::name.get() == "LARGE" && value::ordinal.get() == 1)
        for ((index, entry) in ImportedDurationScale.entries.withIndex()) {
            check(importedEnumOrdinal(entry) == index)
            check(ordinal(entry) == index)
        }
        check(ImportedDurationScale.SMALL.label() == "small")
    }
}
