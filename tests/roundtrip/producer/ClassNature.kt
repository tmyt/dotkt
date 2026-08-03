package roundtrip.classnature

fun interface Handler {
    fun handle(value: Int): Int
}

sealed interface Shape
class Circle(val radius: Int) : Shape
class Square(val side: Int) : Shape

fun runHandler(handler: Handler, value: Int): Int = handler.handle(value)
