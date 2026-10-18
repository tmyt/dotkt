import NUnit.Framework.TestAttribute
import nullablepropertyslots.Source
import nullablepropertyslots.DerivedSource

private class ImportedNullableIntProperty(override val item: Int?) : Source<Int?>
private class ImportedNullableLongProperty(override val item: Long?) : DerivedSource<Long?>
private class ImportedNullableStringProperty(override val item: String?) : Source<String?>
private fun <T> readImportedNullableProperty(value: Source<T>): T = value.item

class NullablePropertySlotTests {
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
