package roundtrip.constructorselection

class SelectedConstructor<T> {
    val chosen: Int
    constructor(value: T) { chosen = 1 }
    constructor(marker: Int) { chosen = 2 }
}

class SelectionBox<T>(val value: T)

class NullableSelectionOuter<O>(val outer: O?) {
    inner class Child<U>(val value: U?)
}
class NullableSelectionHolder(val child: NullableSelectionOuter<Int>.Child<String>)
class GenericNullableSelectionHolder<T>(val marker: T, val child: NullableSelectionOuter<Int>.Child<String>)

class CovariantSelectionOuter<out T>(val value: T) {
    inner class Child { fun read(): T = value }
    inner class GenericChild<U>(val own: U) { fun read(): T = value }
    fun child(): Child = Child()
    fun <U> genericChild(own: U): GenericChild<U> = GenericChild(own)
}

class ReceiverSelectedConstructor(val block: String.() -> Int) {
    fun run(value: String): Int = value.block()
}

class SelectionLocalHost<T>(val seed: T) {
    fun checkLocalFrame() {
        class Local<U>(val value: U) { val captured = seed }
        val local = Local(89)
        check(local.value == 89 && local.captured == seed)
    }
}

class NestedSelectedConstructor<T> {
    val chosen: Int
    constructor(box: SelectionBox<T>) { chosen = 1 }
    constructor(number: SelectionBox<Int>) { chosen = 2 }
}

class MultiSelectedConstructor<A, B> {
    val chosen: Int
    constructor(first: A, second: B) { chosen = 1 }
    constructor(number: Int, other: B) { chosen = 2 }
}

class NullableSelectedConstructor<T> {
    val chosen: Int
    constructor(value: T?) { chosen = 1 }
    constructor(number: Int?) { chosen = 2 }
}

class ArraySelectedConstructor<T> {
    val chosen: Int
    constructor(values: Array<T>) { chosen = 1 }
    constructor(strings: Array<String>) { chosen = 2 }
}

class SelectionOuter<T>(val seed: T) {
    inner class Child(val value: T = seed) {
        var chosen = 1
        constructor(marker: Int) : this() { chosen = 2 }
    }
}

fun <T> checkGenericSelection(input: T) {
    check(SelectedConstructor<T>(value = input).chosen == 1)
    check(SelectedConstructor<T>(marker = 11).chosen == 2)
    class Local {
        val captured = input
        val chosen: Int
        constructor(value: T) { chosen = 1 }
        constructor(marker: Int) { chosen = 2 }
    }
    val generic = Local(value = input)
    val concrete = Local(marker = 13)
    check(generic.chosen == 1 && concrete.chosen == 2)
    check(generic.captured == input && concrete.captured == input)
}

fun checkLocalConstructorSelection() {
    check(CovariantSelectionOuter("outer").child().read() == "outer")
    check(CovariantSelectionOuter(101).child().read() == 101)
    val genericChild = CovariantSelectionOuter("outer").genericChild(103)
    check(genericChild.read() == "outer" && genericChild.own == 103)
    SelectionLocalHost("owner").checkLocalFrame()
    SelectionLocalHost(97).checkLocalFrame()
    check(SelectedConstructor<Int>(value = 3).chosen == 1)
    check(SelectedConstructor<Int>(marker = 5).chosen == 2)
    check(SelectedConstructor<String>(value = "text").chosen == 1)
    check(SelectedConstructor<String>(marker = 7).chosen == 2)
    check(SelectedConstructor<Int?>(value = null).chosen == 1)
    check(NestedSelectedConstructor<Int>(box = SelectionBox(17)).chosen == 1)
    check(NestedSelectedConstructor<Int>(number = SelectionBox(19)).chosen == 2)
    check(NestedSelectedConstructor<String>(box = SelectionBox("nested")).chosen == 1)
    check(MultiSelectedConstructor<Int, String>(first = 23, second = "a").chosen == 1)
    check(MultiSelectedConstructor<Int, String>(number = 31, other = "b").chosen == 2)
    check(MultiSelectedConstructor<String, Int>(first = "a", second = 41).chosen == 1)
    check(MultiSelectedConstructor<String, Int>(number = 43, other = 37).chosen == 2)
    check(NullableSelectedConstructor<Int>(value = null).chosen == 1)
    check(NullableSelectedConstructor<Int>(number = null).chosen == 2)
    check(ArraySelectedConstructor<String>(values = arrayOf("generic")).chosen == 1)
    check(ArraySelectedConstructor<String>(strings = arrayOf("concrete")).chosen == 2)
    val outer = SelectionOuter(47)
    check(outer.Child(value = 53).chosen == 1)
    val secondary = outer.Child(marker = 0)
    check(secondary.chosen == 2 && secondary.value == 47)
    checkGenericSelection(59)
    checkGenericSelection("captured")
    checkGenericSelection<Int?>(null)
}
