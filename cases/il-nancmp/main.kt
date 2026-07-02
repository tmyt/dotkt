// float/double `<=`/`>=` must use the UNORDERED-inverted CIL compares (cgt.un/clt.un + invert, C#'s shape):
// the plain signed inversion returned TRUE for a NaN operand. `<`/`>` stay ordered (false on NaN), and the
// ordinary orderings must keep working (integer compares keep the signed opcodes — covered elsewhere).
fun main() {
    val n = Double.NaN
    println(n <= 1.0)        // false (was True)
    println(n >= 1.0)        // false (was True)
    println(n < 1.0)         // false
    println(n > 1.0)         // false
    println(1.0 <= 2.0)      // true
    println(2.0 >= 2.0)      // true
    println(1.0f <= 2.0f)    // true
    println(Float.NaN <= 1.0f) // false
}
