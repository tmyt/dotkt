// Extension-function callable references (unbound `Type::extFn`), used as values and passed to
// higher-order functions. Each lifts to a static forwarder whose BODY is the faithful extension call;
// bir2cir binds/substitutes that inner call like any other. `isNotBlank` is a cross-module stdlib
// extension (@ClrIntrinsic-bound in bir2cir); `shout`/`doubleLen`/`repeatBy` are same-module.
// This closes G8 — the exact shape `Indent.kt` uses (`String::isNotBlank`) once the #72 lambda-wrap
// workaround is reverted.
fun String.shout(): String = uppercase() + "!"
fun String.doubleLen(): Int = length * 2
fun String.repeatBy(n: Int): String = repeat(n)
fun String.logTo(sb: StringBuilder): Unit { sb.append("[").append(this).append("]") }

fun main() {
    val lines = listOf("  hi ", "   ", "world", "")
    // unbound cross-module stdlib extension ref passed to a higher-order fn (the Indent.kt case)
    println(lines.filter(String::isNotBlank).joinToString("|"))   // "  hi |world"
    // same-module unbound extension ref (receiver-only)
    val words = listOf("a", "hey", "hello")
    println(words.map(String::doubleLen).joinToString(","))       // 2,6,10
    // stored in a function-typed val, then invoked
    val f: (String) -> String = String::shout
    println(f("kotlin"))                                          // KOTLIN!
    // a same-module extension ref with a regular param beyond the receiver: (String, Int) -> String
    val g: (String, Int) -> String = String::repeatBy
    println(g("ab", 3))                                           // ababab
    // a Unit-returning extension ref: the forwarder body is an exprStmt (not a return)
    val sb = StringBuilder()
    val h: (String, StringBuilder) -> Unit = String::logTo
    h("a", sb); h("b", sb)
    println(sb.toString())                                        // [a][b]
}
