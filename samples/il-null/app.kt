// IL parity: nullable reference types — elvis ?:, safe call ?., not-null !!, String.length.
fun up(s: String?): String = s?.uppercase() ?: "none"
fun pick(a: String?, b: String): String = a ?: b
fun main() {
    println(up(null))
    println(up("hi"))
    println(pick(null, "fallback"))
    val s: String? = "abc"
    println(s!!.uppercase())
    println("hello".length)
}
