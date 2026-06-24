// Long-tail language features: `field` identifier in custom accessors, `return` used as an expression,
// lateinit var read, and smart-cast (when + type, and a compound `&&` condition).
class Counter {
    var n: Int = 0
        get() = field            // `field` = the backing field
        set(v) { field = v + 1 } // setter adjusts via `field`
}
class Box { lateinit var s: String }

fun pick(x: Any): String = when (x) {
    is Int -> "int:" + (x + 1)        // smart-cast to Int in the branch
    is String -> "str:" + x.length    // smart-cast to String
    else -> "other"
}
fun classify(x: Any): String =
    if (x is Int && x > 10) "big:" + (x - 10)   // compound-condition smart-cast
    else "small"

fun firstPositive(a: Int, b: Int): Int {
    val x = if (a > 0) a else return b           // `return` in expression position
    return x * 100
}

fun main() {
    val c = Counter(); c.n = 5; println(c.n)     // 6  (setter +1, getter via field)
    val box = Box(); box.s = "hi"; println(box.s) // hi (lateinit)
    println(pick(41)); println(pick("abc"))      // int:42 / str:3
    println(classify(15)); println(classify(3))  // big:5 / small
    println(firstPositive(7, 9))                 // 700
    println(firstPositive(-1, 9))                // 9  (return as expr)
}
