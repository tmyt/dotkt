package roundtrip.memberextensionindexers

class Grid(var value: Int)

open class Ops(val bias: Int) {
    open operator fun Grid.get(index: Int): Int = value + bias + index
    open operator fun Grid.set(index: Int, newValue: Int) { value = newValue - bias - index }
    operator fun Grid.get(row: Int, column: Int): Int = value + bias + row + column
    operator fun Grid.set(row: Int, column: Int, newValue: Int) { value = newValue - bias - row - column }
}

class Cell<T>(var value: T)

open class GenericOps<O>(val key: O) {
    operator fun <T> Cell<T>.get(expectedKey: O): T {
        check(expectedKey == key)
        return value
    }
    operator fun <T> Cell<T>.set(expectedKey: O, newValue: T) {
        check(expectedKey == key)
        value = newValue
    }
}
