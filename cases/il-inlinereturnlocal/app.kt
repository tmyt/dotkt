// #31 (kotc): a lambda-LOCAL labeled `return@label expr` in EXPRESSION position must route through inlineReturnSubst
// (a local transfer, wrapped by breakContinueExpr), NOT leak as a raw `{k:returnExpr}`. If it leaks, bir2cir's
// MaterializeCarrier cannot distinguish it from a genuine non-local return and rejects the carrier fail-loud.

// crossinline -> the lambda is captured in a RETURNED closure, so the carrier MUST materialize (the fail-loud path).
inline fun <R> deferred(crossinline block: () -> R): () -> R = { block() }

fun classifyDeferred(n: Int): Int {
    val f = deferred {
        val x: Int = if (n >= 0) n else return@deferred -1   // lambda-local, expr position (if-as-value)
        x * 2
    }
    return f()
}

// direct-invoke carrier (elvis-RHS lambda-local return in expr position)
inline fun <R> runIt(block: () -> R): R = block()

fun classifyRun(s: String?): String {
    return runIt {
        val v: String = s ?: return@runIt "was-null"   // lambda-local, expr position (elvis RHS)
        "got-$v"
    }
}

fun main() {
    println(classifyDeferred(5))    // 10
    println(classifyDeferred(-3))   // -1
    println(classifyRun("hi"))      // got-hi
    println(classifyRun(null))      // was-null
}
