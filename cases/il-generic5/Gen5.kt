// Generic indexer: operator get/set on a generic class (just instance methods on a constructed generic).
class Slot<T>(var a: T, var b: T) {
    operator fun get(i: Int): T = if (i == 0) a else b
    operator fun set(i: Int, v: T) { if (i == 0) a = v else b = v }
}

fun main() {
    val s = Slot(10, 20)
    println(s[0])       // 10
    println(s[1])       // 20
    s[1] = 99
    println(s[1])       // 99
    val t = Slot("x", "y")
    t[0] = "z"
    println(t[0])       // z
}
