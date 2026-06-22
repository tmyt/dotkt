// enum with per-entry bodies overriding an abstract member -> abstract base enum + one subclass per entry.
enum class Op(val sym: String) {
    PLUS("+")  { override fun apply(a: Int, b: Int) = a + b },
    MINUS("-") { override fun apply(a: Int, b: Int) = a - b },
    TIMES("*") { override fun apply(a: Int, b: Int) = a * b };
    abstract fun apply(a: Int, b: Int): Int
}
fun main() {
    for (op in Op.values()) println(op.sym + ": " + op.apply(6, 2))  // +: 8 / -: 4 / *: 12
    println(Op.PLUS.name)            // PLUS
    println(Op.valueOf("TIMES").apply(3, 3))  // 9
}
