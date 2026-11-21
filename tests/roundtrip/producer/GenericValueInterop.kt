package roundtrip.genericvalueinterop

class Box<T>(var value: T)
fun createBox(): Box<String> = Box("initial")
fun boxVector(value: Box<String>): Array<Box<String>> = arrayOf(value)
fun <T> replaceFirstElement(values: Array<T>, value: T) { values[0] = value }
fun <T> firstArrayElement(values: Array<T>): T = values[0]
fun <T> firstIteratedElement(values: Array<T>): T {
    for (value in values) return value
    error("empty array")
}
class Cell<T>(@kotlin.clr.ClrField var value: T)

class Selected {
    val tag: String
    constructor(value: Box<String>) { tag = "string" }
    constructor(value: Box<Int>) { tag = "int" }
}

open class Base
class Derived<T> : Base()
