// Kotlin `x.toString()` on a nullable receiver -> the null-safe `Any?.toString()` extension ("null" when null).
fun main() {
    val s: String? = null
    println(s.toString())          // null
    val t: String? = "abc"
    println(t.toString())          // abc
    val sb: StringBuilder? = null
    println(sb.toString())         // null
    println("v=" + s.toString())   // v=null
}
