import NUnit.Framework.TestAttribute

private fun <T> varianceNarrow(callback: (Any?) -> Any?): (T) -> Any? = callback
private fun <T, R> varianceWiden(callback: (T) -> R): (T) -> Any? = callback
private fun <T, R> varianceNullable(callback: ((T) -> R)?): ((T) -> Any?)? = callback
private fun <T> varianceInvoke(callback: (T) -> Any?, value: T): Any? = callback(value)
private class VarianceStorage<T>(var callback: (T) -> Any?)
private class VarianceOverloads {
    fun select(callback: (Int) -> Unit): Int { callback(17); return 1 }
    fun select(callback: (String) -> Unit): Int { callback("selected"); return 2 }
}
private class VarianceText(private val text: String) : CharSequence {
    override val length: Int get() = text.length
    override fun get(index: Int): Char = text[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence =
        VarianceText(text.substring(startIndex, endIndex))
}

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
        val sequenceLength: (CharSequence) -> Int = { it.length }
        check(sequenceLength("abc") == 3)
        check(sequenceLength(VarianceText("four")) == 4)
        check(sequenceLength(StringBuilder("builder")) == 7)
        val nullableLength: (CharSequence?) -> Int? = { it?.length }
        check(nullableLength(null) == null)
        check(nullableLength("ab") == 2)
        val anyValue: (Any) -> Boolean = { it is String }
        val sequenceView: (CharSequence) -> Boolean = anyValue
        check(sequenceView === anyValue)
        check(sequenceView("raw string"))
        var selectedNumber = 0
        var selectedText = ""
        val numberCallback: (Int) -> Unit = { selectedNumber = it }
        val textCallback: (String) -> Unit = { selectedText = it }
        val overloads = VarianceOverloads()
        check(overloads.select(numberCallback) == 1)
        check(overloads.select(textCallback) == 2)
        check(selectedNumber == 17)
        check(selectedText == "selected")
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
        var evaluations = 0
        val textFactory: () -> String = { evaluations++; "result" }
        val sequenceFactory: () -> CharSequence = textFactory
        check(sequenceFactory === textFactory)
        check(sequenceFactory().length == 6)
        check(evaluations == 1)
        val nullableTextFactory: () -> String? = { null }
        val nullableSequenceFactory: () -> CharSequence? = nullableTextFactory
        check(nullableSequenceFactory === nullableTextFactory)
        check(nullableSequenceFactory() == null)
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
