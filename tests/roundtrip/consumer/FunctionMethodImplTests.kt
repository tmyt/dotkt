import NUnit.Framework.TestAttribute
import roundtrip.functionslots.*

private class ImportedFunctionSlots<T> : FunctionBody<T>(), FunctionSlot<T>
private class IntFunctionSlots : FunctionBody<Int>(), FunctionSlot<Int>
private class NullableIntFunctionSlots : FunctionBody<Int?>(), FunctionSlot<Int?>
private class StringFunctionSlots : FunctionBody<String>(), FunctionSlot<String>

private fun <T> checkFunctionOwner(value: T) {
    val local = LocalFunctionSlots<T>()
    val imported = ImportedFunctionSlots<T>()
    checkFunctionSlots(local, local, value)
    checkFunctionSlots(imported, imported, value)
    check(imported.apply(value) { it } == value)
    check(imported.extension(value) { this } == value)
}

class FunctionMethodImplTests {
    @TestAttribute
    fun inheritedFunctionSlotsMatchAcrossDlls() {
        checkFunctionOwner(31)
        checkFunctionOwner<Int?>(null)
        checkFunctionOwner<Int?>(33)
        checkFunctionOwner("value")
        checkFunctionOwner<String?>(null)
        val ints = IntFunctionSlots()
        checkFunctionSlots(ints, ints, 37)
        check(ints.apply(41) { it } == 41)
        val nullable = NullableIntFunctionSlots()
        checkFunctionSlots(nullable, nullable, null)
        checkFunctionSlots(nullable, nullable, 43)
        val strings = StringFunctionSlots()
        checkFunctionSlots(strings, strings, "closed")
    }
}
