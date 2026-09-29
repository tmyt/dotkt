import NUnit.Framework.TestAttribute
import roundtrip.functionslots.*

private class ImportedFunctionSlots<T> : FunctionBody<T>(), FunctionSlot<T>

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
    }
}
