import NUnit.Framework.TestAttribute

private fun <T> varianceNarrow(callback: (Any?) -> Any?): (T) -> Any? = callback
private fun <T, R> varianceWiden(callback: (T) -> R): (T) -> Any? = callback
private fun <T, R> varianceNullable(callback: ((T) -> R)?): ((T) -> Any?)? = callback
private fun <T> varianceInvoke(callback: (T) -> Any?, value: T): Any? = callback(value)
private class VarianceStorage<T>(var callback: (T) -> Any?)
private fun <T> varianceReadResult(values: List<T>): Any? {
    val callback: () -> T = { values[0] }
    return callback()
}
private fun <T> varianceStoredResult(values: List<T>): Any? {
    val callback: () -> T = { values[0] }
    val result: T = callback()
    return result
}
private fun <T> varianceInvokeResult(callback: () -> T): Any? = callback()
private fun <T> variancePassParameter(values: List<T>): Any? {
    val callback: (T) -> Any? = { check(values.size == 1); it }
    return callback(values[0])
}
private fun <T> varianceNativeParameter(values: List<T>): Int {
    val destination = System.Collections.Generic.List<T>()
    val callback: (T) -> Unit = { destination.Add(it) }
    callback(values[0])
    return destination.Count
}
private fun <T : CharSequence> varianceParameterLength(values: List<T>): Int {
    val callback: (T) -> Int = { check(values.size == 1); it.length }
    return callback(values[0])
}
private fun <T : CharSequence> varianceBoundLength(callback: () -> T): Int = callback().length
private fun <T> varianceExactResult(values: List<T>): T {
    val callback: () -> T = { values[0] }
    return callback()
}
private fun <T : Number> varianceBoundResult(values: List<T>): Number {
    val callback: () -> T = { values[0] }
    return callback()
}
private fun <T : Number> varianceExplicitBound(callback: () -> T): Any? = callback() as T
private fun <T> varianceNativeResult(values: List<T>): Any? {
    val callback = System.Func1<T> { values[0] }
    return callback()
}
private class VarianceResultOwner<A>(private val marker: A) {
    fun <T> read(values: List<T>): Any? {
        val callback: () -> T = { check(marker != null); values[0] }
        return callback()
    }
}
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
    @Suppress("UNCHECKED_CAST")
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
        val heterogeneous = listOf("heterogeneous") as List<Int>
        check(variancePassParameter(heterogeneous) == "heterogeneous")
        check(variancePassParameter<Int>(listOf<Any?>(null) as List<Int>) == null)
        check(variancePassParameter(listOf(17)) == 17)
        check(varianceNativeParameter(listOf(23)) == 1)
        var rejected = false
        try { varianceNativeParameter(heterogeneous) }
        catch (_: ClassCastException) { rejected = true }
        check(rejected)
        check(varianceParameterLength<CharSequence>(listOf("four")) == 4)
        check(varianceParameterLength<CharSequence>(listOf(VarianceText("five"))) == 4)
    }

    @TestAttribute
    @Suppress("UNCHECKED_CAST")
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
        val heterogeneous = listOf("heterogeneous") as List<Int>
        check(varianceReadResult(heterogeneous) == "heterogeneous")
        check(varianceStoredResult(heterogeneous) == "heterogeneous")
        check(VarianceResultOwner("owner").read(heterogeneous) == "heterogeneous")
        val absent = listOf<Any?>(null) as List<Int>
        check(varianceReadResult(absent) == null)
        check(varianceStoredResult(absent) == null)
        var resultCalls = 0
        val rawResult: () -> Any? = { resultCalls++; "heterogeneous" }
        check(varianceInvokeResult<Int>(rawResult as () -> Int) == "heterogeneous")
        check(resultCalls == 1)
        var boundCalls = 0
        check(varianceBoundLength { boundCalls++; "four" } == 4)
        check(boundCalls == 1)
        check(varianceBoundLength { VarianceText("five") } == 4)
        check(varianceExactResult(listOf(19)) == 19)
        var rejected = false
        try { val exact: Int = varianceExactResult(heterogeneous); check(exact == 0) }
        catch (_: ClassCastException) { rejected = true }
        check(rejected)
        check(varianceBoundResult<Int>(listOf(4.5) as List<Int>).toInt() == 4)
        rejected = false
        try { varianceBoundResult(heterogeneous).toInt() }
        catch (_: ClassCastException) { rejected = true }
        check(rejected)
        rejected = false
        try { varianceExplicitBound<Number>(rawResult as () -> Number) }
        catch (_: ClassCastException) { rejected = true }
        check(rejected)
        check(varianceNativeResult(listOf(23)) == 23)
        rejected = false
        try { varianceNativeResult(heterogeneous) }
        catch (_: ClassCastException) { rejected = true }
        check(rejected)
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
