package unitvalues

var trace: String = ""
fun effect(tag: String) { trace += tag }
fun reset() { trace = "" }
fun <T> identity(value: T): T = value
class Receiver {
    fun write() { effect("m") }
}
