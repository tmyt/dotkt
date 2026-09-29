package roundtrip.innerconstruction

class ConstructionCell<T>(val value: T)

open class ConstructionOuter<T> {
    val storage = ConstructionCell<T?>(null)
    open inner class Base<U>(val own: U, val outerValue: T)
}

class ConstructionDerived<A, B> : ConstructionOuter<B>() {
    inner class Child(own: A, value: B) : Base<A>(own, value)
}

fun <A, B> checkConstructedChild(child: ConstructionDerived<A, B>.Child, own: A, value: B) {
    check(child.own == own && child.outerValue == value)
}

fun <A, B> checkLocalConstruction(own: A, value: B) {
    val child = ConstructionDerived<A, B>().Child(own, value)
    check(child.own == own && child.outerValue == value)
}

class ConstructionHost<A> {
    fun <B> check(own: A, value: B) {
        val child = ConstructionDerived<A, B>().Child(own, value)
        kotlin.check(child.own == own && child.outerValue == value)
    }
}
