private class ClrNameConstructorSignatureCollision {
    constructor(values: Array<Int>) { println(values.size) }
    constructor(values: IntArray) { println(values.size) }
}

fun main(): Unit = Unit
