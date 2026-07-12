// BOUND extension-function references (`expr::extFn`) — the receiver is captured ONCE, eagerly, at
// reference-creation time. Each lifts to a CAPTURE CLASS (a synth closure with a `__recv` field forwarding
// to the ext fn), exactly like a capturing lambda `{ args -> expr.extFn(args) }`. The delegate binds over the
// closure's INSTANCE `invoke` (ldftn instance + newobj) — ilverify-clean, unlike a closed static delegate.
fun String.shout(): String = this + "!"
fun String.repeatBy(n: Int): String = repeat(n)
fun String.logTo(sb: StringBuilder): Unit { sb.append("[").append(this).append("]") }

fun main() {
    // receiver-only bound ext ref, stored and invoked
    val f = "hi"::shout
    println(f())                        // hi!

    // bound ext ref with a regular param beyond the receiver: (Int) -> String
    val g = "ab"::repeatBy
    println(g(3))                       // ababab

    // the receiver is captured EAGERLY: mutate the var after taking the ref -> ref still uses "first"
    var s = "first"
    val h = s::shout
    s = "second"
    println(h())                        // first!

    // a Unit-returning bound ext ref: the forwarder body is an exprStmt (not a return)
    val sb = StringBuilder()
    val u = "x"::logTo
    u(sb); u(sb)
    println(sb.toString())              // [x][x]

    // #106: a BOUND ref to a CharSequence-declared stdlib ext (`expr::isNotBlank`/`::isBlank`). The String receiver is
    // captured into the closure's `__recv` field; the forwarder invokes `StringsKt.isNotBlank(this.__recv)` whose param
    // is the `dotkt$CharSequence` adapter. StringCharSequenceBridge now recognizes the String-typed FIELD read and wraps
    // it as `dotkt$StringCharSequence`, so the stdlib CharSequence body runs (was: raw String -> ilverify/JIT crash).
    val nb = "  x "::isNotBlank
    println(nb())                       // True
    val bl = "   "::isBlank
    println(bl())                       // True
}
