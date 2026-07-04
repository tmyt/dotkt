// Kotlin renders a NULL interpolated / concatenated value as the string "null" (JVM:
// StringBuilder.append(Any?)/String.valueOf -> "null"), NOT an empty append. A bare CLR
// String.Concat/StringBuilder.Append of a null ref would yield "" — so kotc routes a NULLABLE
// concat operand through the stdlib null-safe stringifier `Any?.toString()`
// (kotlin.LibraryKt.toString = `this?.toString() ?: "null"`) before it is concatenated. This
// makes `"$x"` agree with `x.toString()` and `println(x)` for null (bundle-6 null-render FIX).
fun main() {
    val x: Any? = null
    println("[$x]")                 // string template, null Any?      -> [null]
    val n: Int? = null
    println("n=$n")                 // string template, null Int?      -> n=null
    println("" + x)                 // string `+` concat, null Any?    -> null
    val s: String? = null
    println("s=$s end")             // string template, null String?   -> s=null end
    val a = 5
    println("a=$a")                 // non-null value, unchanged       -> a=5
    val nn: Int? = 7
    println("nn=$nn")               // non-null nullable value         -> nn=7
    val m = mapOf("k" to 1)
    println("m=$m")                 // Map operand keeps Kotlin-style  -> m={k=1}
}
