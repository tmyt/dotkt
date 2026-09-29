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

class CapturedConstructionOuter<T> {
    val storage = ConstructionCell<T?>(null)
    inner class Child
    inner class ValueChild(val value: T)
    fun make(): Child {
        fun local(): Child = Child()
        return local()
    }
    fun makeValue(input: T): ValueChild {
        fun local(): ValueChild = ValueChild(input)
        return local()
    }
    fun defaultValue(input: T, child: ValueChild = ValueChild(input)): ValueChild = child
    fun <M> localBox(input: M): Any {
        val captured = storage
        class Local(val value: M) { val outer = captured }
        return Local(input)
    }
}

class LambdaConstructionOuter<A, B> {
    val storage = ConstructionCell<B?>(null)
    inner class Child(val own: A, val value: B)
}

class LambdaConstructionHost<A> {
    fun <B> make(own: A, value: B): Any {
        val create = { outer: LambdaConstructionOuter<A, B>, a: A, b: B -> outer.Child(a, b) }
        return create(LambdaConstructionOuter<A, B>(), own, value)
    }
}

fun <A> checkDefaultConstruction(input: A) {
    val child = CapturedConstructionOuter<A>().defaultValue(input)
    check(child.value == input)
}

fun checkCapturedConstructionFrames() {
    val text: Any = CapturedConstructionOuter<String>().make()
    val number: Any = CapturedConstructionOuter<Int>().make()
    val nullable: Any = CapturedConstructionOuter<Int?>().make()
    check(text !== number && number !== nullable)
    check(CapturedConstructionOuter<String>().makeValue("text").value == "text")
    check(CapturedConstructionOuter<Int>().makeValue(61).value == 61)
    check(CapturedConstructionOuter<Int?>().makeValue(null).value == null)
    check(LambdaConstructionHost<Int>().make(17, "text") !== LambdaConstructionHost<String>().make("own", 23))
    LambdaConstructionHost<Int?>().make<String?>(null, null)
    CapturedConstructionOuter<Int>().localBox("local")
    CapturedConstructionOuter<String?>().localBox<Int?>(null)
    checkDefaultConstruction("default")
    checkDefaultConstruction(67)
    checkDefaultConstruction<Int?>(null)
}
