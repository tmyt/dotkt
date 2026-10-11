import NUnit.Framework.TestAttribute
import roundtrip.functionslots.*

private class ImportedFunctionSlots<T> : FunctionBody<T>(), FunctionSlot<T>
private class IntFunctionSlots : FunctionBody<Int>(), FunctionSlot<Int>
private class NullableIntFunctionSlots : FunctionBody<Int?>(), FunctionSlot<Int?>
private class StringFunctionSlots : FunctionBody<String>(), FunctionSlot<String>
private class ImportedGenericCallbackSlots<T> : GenericCallbackBody<T>()

private fun <T> checkGenericCallbackSlots(value: T) {
    val imported = ImportedGenericCallbackSlots<T>()
    val slot: GenericCallbackSlot<T> = imported
    check(imported.complete(value, "ordinary") == "ordinary")
    check(slot.complete(value, "interface") == "interface")

    var called = 0
    val callback: (Throwable, T, Any) -> Unit = { cause, received, token ->
        check(cause.message == "callback")
        check(received == value)
        check(token == "generic")
        called++
    }
    check(imported.complete<T>(value, "generic", callback) == value)
    check(slot.complete<T>(value, "generic", callback) == value)
    check(slot.complete<T>(value, null, null) == value)
    check(called == 2)
}

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
    fun genericCallbackSlotUsesTheDeclarationMethodFrameAcrossDlls() {
        checkGenericCallbackSlots("value")
        checkGenericCallbackSlots(17)
        checkGenericCallbackSlots<String?>(null)
        checkGenericCallbackSlots<Int?>(null)
        checkGenericCallbackSlots<Int?>(19)
    }

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
