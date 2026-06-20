// A: enum rich API — name, ordinal, valueOf, values(), entries.
enum class Color { RED, GREEN, BLUE }

fun main() {
    val c = Color.GREEN
    println(c.name)
    println(c.ordinal)
    println(Color.valueOf("BLUE"))
    for (x in Color.values()) println(x)
    println(Color.entries.size)
}
