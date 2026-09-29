package roundtrip.innerresults

class ResultCell<T>(val value: T)

open class ResultOuter<T> {
    val nullableStorage = ResultCell<T?>(null)
    open inner class Base<U>(val own: U, val value: T) {
        fun readOwn(): U = own
        fun readValue(): T = value
        fun nullableOwn(): U? = own
        fun nullableValue(): T? = value
        fun ownCell(): ResultCell<U?> = ResultCell(own)
        fun valueCell(): ResultCell<T?> = ResultCell(value)
        fun <M> identity(input: M): M = input
    }
}

class ClosedResult : ResultOuter<String>() {
    inner class Child : Base<Int>(17, "value")
}

open class ResultBridge<X, Y> : ResultOuter<Y>()
class GenericResult<X, Y> : ResultBridge<X, Y>() {
    inner class Child(own: X, value: Y) : Base<X>(own, value)
}

fun <A, B, M> checkLocalInnerResult(own: A, value: B, input: M) {
    val child = GenericResult<A, B>().Child(own, value)
    check(child.own == own && child.value == value)
    check(child.readOwn() == own && child.readValue() == value)
    check(child.nullableOwn() == own && child.nullableValue() == value)
    check(child.ownCell().value == own && child.valueCell().value == value)
    check(child.identity(input) == input)
}

class ResultHost<A, B> {
    fun check(own: A, value: B) {
        val child = GenericResult<A, B>().Child(own, value)
        kotlin.check(child.own == own && child.value == value)
        kotlin.check(child.readOwn() == own && child.readValue() == value)
        kotlin.check(child.nullableOwn() == own && child.nullableValue() == value)
        kotlin.check(child.ownCell().value == own && child.valueCell().value == value)
    }
}

fun checkLocalInnerResults() {
    val child = ClosedResult().Child()
    check(child.value == "value" && child.own == 17)
    check(child.readValue() == "value" && child.readOwn() == 17)
    checkLocalInnerResult("own", 23, true)
    checkLocalInnerResult(31, "outer", "method")
    checkLocalInnerResult<Int?, String?, Boolean?>(null, null, null)
    ResultHost<Int, String>().check(41, "class")
    ResultHost<String?, Int?>().check(null, null)
}
