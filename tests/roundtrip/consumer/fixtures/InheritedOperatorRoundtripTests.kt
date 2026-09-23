import NUnit.Framework.TestAttribute
import roundtrip.genop.Arr

private class ImportedOperatorChild<T>(values: Array<T>) : Arr<T>(values) {
    fun captured(): () -> T = { this[0] }
}

class InheritedOperatorRoundtripTests {
    @TestAttribute
    fun inheritedGenericOperatorsKeepTheKotlinDeclaration() {
        val strings = ImportedOperatorChild(arrayOf("initial"))
        val captured = strings.captured()
        check(strings[0] == "initial")
        strings[0] = "changed"
        check(strings[0] == "changed")
        check(captured() == "changed")
        val integers = ImportedOperatorChild(arrayOf(19))
        check(integers[0] == 19)
        integers[0] = 37
        check(integers.captured()() == 37)
    }
}
