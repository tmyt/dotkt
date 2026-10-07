package roundtrip.genericvalueinterop

class Box<T>(var value: T)
class Cell<T>(@kotlin.clr.ClrField var value: T)

fun replaceTextBoxRef(slot: kotlin.clr.ClrRef<Box<String>>, value: Box<String>) {
    slot.value = value
}

fun <T> replaceBoxRef(slot: kotlin.clr.ClrRef<Box<T>>, value: Box<T>) {
    slot.value = value
}

fun replaceNamesRef(slot: kotlin.clr.ClrRef<List<String>>, value: List<String>) {
    slot.value = value
}

fun replaceBoxesRef(slot: kotlin.clr.ClrRef<Array<Box<String>>>, value: Array<Box<String>>) {
    slot.value = value
}

class Selected {
    val tag: String
    constructor(value: Box<String>) { tag = "string" }
    constructor(value: Box<Int>) { tag = "int" }
}

open class Base
class Derived<T> : Base()
