import BidirectionalInterop.Palette

class BidirectionalGreeter(val name: String) {
    fun greet(): String = "Hi, $name (accent=${Palette().Accent})"
    fun roster(): List<String> = listOf("$name A", "$name B", "$name C")
}

fun bidirectionalAdd(a: Int, b: Int): Int = a + b
