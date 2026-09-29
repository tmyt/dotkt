import NUnit.Framework.TestAttribute

private fun <T> varianceNarrow(callback: (Any?) -> Any?): (T) -> Any? = callback
private fun <T, R> varianceWiden(callback: (T) -> R): (T) -> Any? = callback
private fun <T, R> varianceNullable(callback: ((T) -> R)?): ((T) -> Any?)? = callback
private fun <T> varianceInvoke(callback: (T) -> Any?, value: T): Any? = callback(value)
private class VarianceStorage<T>(var callback: (T) -> Any?)

class FunctionVarianceTests {
    @TestAttribute
    fun argumentViewRetainsIdentity() {
        val identity: (Any?) -> Any? = { it }
        val integers = varianceNarrow<Int>(identity)
        val strings = varianceNarrow<String>(identity)
        val nullable = varianceNarrow<Int?>(identity)
        check(integers(17) == 17)
        check(strings("text") == "text")
        check(nullable(null) == null)
        check(nullable(23) == 23)
        check(integers === identity)
        check(strings === identity)
        check(nullable === identity)
    }

    @TestAttribute
    fun resultViewRetainsIdentity() {
        val length: (String) -> Int = { it.length }
        val widened = varianceWiden(length)
        check(widened("hello") == 5)
        check(varianceInvoke(length, "abc") == 3)
        check(widened === length)
        val maybeLength: (String?) -> Int? = { it?.length }
        val maybeWidened = varianceWiden(maybeLength)
        check(maybeWidened(null) == null)
        check(maybeWidened("ab") == 2)
        check(maybeWidened === maybeLength)
    }

    @TestAttribute
    fun storedViewsKeepTheOriginalObject() {
        val identity: (Any?) -> Any? = { it }
        val storage = VarianceStorage<Int>(identity)
        check(storage.callback(9) == 9)
        check(storage.callback === identity)
        val increment: (Int) -> Int = { it + 1 }
        storage.callback = increment
        check(storage.callback(9) == 10)
        check(storage.callback === increment)
        val originalAsAny: Any = increment
        val storedAsAny: Any = storage.callback
        check(originalAsAny === storedAsAny)
    }

    @TestAttribute
    fun nullableFunctionViewsPreserveNullAndIdentity() {
        check(varianceNullable<Int, Int>(null) == null)
        val increment: (Int) -> Int = { it + 1 }
        val view = varianceNullable(increment)
        check(view != null)
        check(view(4) == 5)
        check(view === increment)
    }

    @TestAttribute
    fun unitResultViewsRetainIdentityAndReturnTheSingleton() {
        var total = 0
        val update: (Int) -> Unit = { total += it }
        val view = varianceWiden(update)
        check(view(3) === Unit)
        check(total == 3)
        check(view === update)
        val originalAsAny: Any = update
        val viewAsAny: Any = view
        check(originalAsAny === viewAsAny)
    }
}
