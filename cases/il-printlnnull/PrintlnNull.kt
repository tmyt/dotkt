// println/print of a null value must render the string "null" (Kotlin semantics), not an empty line.
// Non-null values print normally. Covers the literal null, a null Int?/String? local, and non-null Int/String.
fun main() {
    println(null)            // null
    val a: Int? = null
    print(a)                 // null
    print(5)                 // 5
    println("x")             // x   -> line 2 = "null5x"
    val s: String? = null
    println(s)               // null
}
