package ordinarynullablesharedcell

import NUnit.Framework.TestAttribute

private fun interface Setter<T> { fun set(value: T) }

private fun <T> capture(value: T): T? {
    var result: T? = null
    val setter = Setter<T> { result = it }
    setter.set(value)
    return result
}

private fun <T> delayed(first: T, second: T): T? {
    var result: T? = null
    val set: (T) -> Unit = { result = it }
    set(first)
    check(result == first)
    set(second)
    return result
}

class NullableSharedCellTests {
    @TestAttribute
    fun ordinarySamRetainsDeclaredCellParameters() {
        check(capture("text") == "text")
        check(capture(42) == 42)
        check(capture<Int?>(null) == null)
    }

    @TestAttribute
    fun ordinaryFunctionClosureRetainsDeclaredCellParameters() {
        check(delayed("first", "last") == "last")
        check(delayed(1, 42) == 42)
        check(delayed<Int?>(1, null) == null)
    }
}
