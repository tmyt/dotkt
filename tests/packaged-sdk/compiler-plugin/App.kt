package fixture

class Receiver
class Box<T>(val value: T) {
    fun empty(): Boolean = value == null
}
fun construct(): () -> Box<String> = { Box("closed") }
fun <T> constructLocal(t: T): String {
    class Local<U>(val value: U, val captured: T)
    return Local("local", t).value
}
fun <T> constructInBody(): () -> Boolean = { Box<T?>(null).empty() }
fun answer() = Receiver().generatedAnswer()
