import NUnit.Framework.TestAttribute
import roundtrip.multiindex.Slot

private class ImportedMultiIndexSlot<T>(value: T) : Slot<T>(value) {
    fun captured(): () -> T = { this[7, "captured"] }
}

class MultiIndexOperatorRoundtripTests {
    @TestAttribute
    fun importedGenericOperatorsKeepAllArguments() {
        val strings = ImportedMultiIndexSlot<String?>(null)
        check(strings[2, "read"] == null)
        check(strings.lastRow == 2 && strings.lastColumn == "read")
        strings[3, "write"] = "changed"
        check(strings.lastRow == 3 && strings.lastColumn == "write")
        check(strings.captured()() == "changed")
        check(strings.lastRow == 7 && strings.lastColumn == "captured")
        val integers = ImportedMultiIndexSlot(19)
        integers[5, "value"] = 37
        check(integers[6, "read"] == 37)
        check(integers.lastRow == 6 && integers.lastColumn == "read")
    }
}
