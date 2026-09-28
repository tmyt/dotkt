import NUnit.Framework.TestAttribute
import roundtrip.unitcallback.*

private class UnitCallbackHolder<T>(val value: T) {
    fun read(): T = value
}

class GenericUnitCallbackTests {
    @TestAttribute
    fun unitCallbacksAcrossInlineMetadataKeepValidDelegateSignatures() {
        check(sameModuleUnitCallbacks() == 3)
        var calls = 0
        selectedCallback({ calls++; Unit })
        check(invokedCallback({ calls++ }, Unit) == Unit)
        check(capturedCallback({ calls++ }, Unit) == Unit)
        check(calls == 3)
        check(invokedCallback({}, "text") == "text")
        check(capturedCallback({}, 17) == 17)
        check(capturedCallback<String?>({}, null) == null)
        check(invokedCallback<Unit?>({}, null) == null)
        val tracked = CallbackValue(Unit)
        val deferred = deferredCallback({ calls++ }, tracked)
        check(calls == 4 && tracked.calls == 0)
        check(deferred() == Unit)
        check(tracked.calls == 1)
        check(deferred() == Unit)
        check(tracked.calls == 2)
    }

    @TestAttribute
    fun callableReferencesAndExplicitUnitCallbacksRetainTheirContracts() {
        var calls = 0
        check(capturedCallback({ calls++ }, Unit, { calls++; Unit }) == Unit)
        val holder = UnitCallbackHolder(Unit)
        val read = holder::read
        check(read() == Unit)
        check(capturedCallback({}, Unit, read) == Unit)
        val text = UnitCallbackHolder("reference")::read
        check(text() == "reference")
        check(calls == 2)
    }
}
