package roundtrip.genericvalueinterop

class Box<T>(var value: T)
class Cell<T>(@kotlin.clr.ClrField var value: T)

class Selected {
    val tag: String
    constructor(value: Box<String>) { tag = "string" }
    constructor(value: Box<Int>) { tag = "int" }
}

open class Base
class Derived<T> : Base()
