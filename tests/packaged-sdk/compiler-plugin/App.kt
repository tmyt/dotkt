package fixture

class Receiver
class Box<T>(val value: T)
fun construct(): () -> Box<String> = { Box("closed") }
fun answer() = Receiver().generatedAnswer()
