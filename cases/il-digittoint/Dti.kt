// Char.digitToInt / digitToIntOrNull family — value-type-nullable (Int?) return codegen.
// digitToIntOrNull() lowers (via inline takeIf) to `if (di >= 0) di else null`, a `Int?` expression.
fun main() {
    println('7'.digitToIntOrNull())      // 7
    println('a'.digitToIntOrNull(16))    // 10
    println('z'.digitToIntOrNull())      // null
    println('7'.digitToInt())            // 7
}
