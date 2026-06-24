// Overloaded user functions must resolve by name + parameter signature. ilemit keyed methods by name alone, so
// `render(String)` / `render(() -> String)` collided — a body got emitted into the wrong overload's MethodBuilder
// (the WinUI DSL `text(String)` / `text(() -> String)` crash: the Func got assigned to TextBlock.Text).
fun render(s: String): String = "S:" + s
fun render(f: () -> String): String = "F:" + f()
fun render(n: Int): String = "I:" + n
class Box {
    fun put(s: String): String = "bs:" + s
    fun put(f: () -> String): String = "bf:" + f()
}
fun main() {
    println(render("x"))      // S:x
    println(render { "y" })   // F:y
    println(render(7))        // I:7
    val b = Box()
    println(b.put("p"))       // bs:p
    println(b.put { "q" })    // bf:q
}
