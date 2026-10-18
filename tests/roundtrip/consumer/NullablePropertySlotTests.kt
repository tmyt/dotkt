import NUnit.Framework.TestAttribute
import nullablepropertyslots.Source
import nullablepropertyslots.DerivedSource
import nullablepropertyslots.MutableSource
import nullablepropertyslots.BaseSource

private class ImportedNullableIntProperty(override val item: Int?) : Source<Int?>
private class ImportedNullableLongProperty(override val item: Long?) : DerivedSource<Long?>
private class ImportedNullableStringProperty(override val item: String?) : Source<String?>
private class ImportedMutableNullableProperty(override var item: Int?) : MutableSource<Int?>
private class ImportedAbstractNullableProperty(override val item: Int?) : BaseSource<Int?>()
private fun <T> readImportedNullableProperty(value: Source<T>): T = value.item

class NullablePropertySlotTests {
    @TestAttribute
    fun importedMutableNullablePropertySlotsRoundTripWrites() {
        val direct = ImportedMutableNullableProperty(42)
        val slot: MutableSource<Int?> = direct
        check(slot.item == 42)
        slot.item = null
        check(direct.item == null)
        slot.item = 17
        check(direct.item == 17 && slot.item == 17)
    }

    @TestAttribute
    fun importedAbstractNullablePropertySlotsFillClassOverrides() {
        val present = ImportedAbstractNullableProperty(42)
        val absent = ImportedAbstractNullableProperty(null)
        val presentSlot: BaseSource<Int?> = present
        val absentSlot: BaseSource<Int?> = absent
        check(present.item == 42 && absent.item == null)
        check(presentSlot.item == 42 && absentSlot.item == null)
    }

    @TestAttribute
    fun importedNullablePropertySlotsPreservePresentAndNullValues() {
        val present = ImportedNullableIntProperty(42)
        val absent = ImportedNullableIntProperty(null)
        check(present.item == 42 && absent.item == null)
        val presentSlot: Source<Int?> = present
        val absentSlot: Source<Int?> = absent
        check(presentSlot.item == 42 && absentSlot.item == null)
        check(readImportedNullableProperty<Int?>(present) == 42)
        check(readImportedNullableProperty<Int?>(absent) == null)
        check(readImportedNullableProperty<Long?>(ImportedNullableLongProperty(42000000000L)) == 42000000000L)
        check(readImportedNullableProperty<Long?>(ImportedNullableLongProperty(null)) == null)
        check(readImportedNullableProperty<String?>(ImportedNullableStringProperty("present")) == "present")
        check(readImportedNullableProperty<String?>(ImportedNullableStringProperty(null)) == null)
    }
}
