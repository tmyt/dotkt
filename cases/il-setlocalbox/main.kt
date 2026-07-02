class Holder {
    var v: Any = "s"
    fun put(n: Int) { v = n }
}
fun main() {
    var a: Any = "x"
    a = 42
    println(a)
    val h = Holder()
    h.put(7)
    println(h.v)
}
