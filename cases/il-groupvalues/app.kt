fun main() {
    val m = Regex("(a)(b)(c)").find("abc")!!
    println(m.groupValues.joinToString(","))   // abc,a,b,c
    val m2 = Regex("(\\d+)-(\\d+)").find("12-34")!!
    val (x, y) = m2.destructured
    println("$x $y")                            // 12 34
}
