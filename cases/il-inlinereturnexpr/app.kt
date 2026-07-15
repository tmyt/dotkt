// #30 (bir2cir inline splice) — an EXPRESSION-position `return` in a spliced inline fn's body that calls one of
// its lambda params. Covers the three value-position return shapes: elvis RHS (`x ?: return onClosed()`),
// if-as-value (`val x = if (c) a else return onClosed()`), and when-as-value (`else -> return onClosed()`).
// Each inline fn ALSO ends in a statement-position `return onClosed()`, so every call exercises BOTH the early
// (expression-position, routed to the splice result-local + end-label) and the fall-through return branch.

// elvis RHS
inline fun <R> elvisImpl(input: String?, onClosed: () -> R): R {
    val x: String = input ?: return onClosed()
    println("elvis-body $x")
    return onClosed()
}

// if-as-value
inline fun <R> ifImpl(c: Boolean, onClosed: () -> R): R {
    val x: Int = if (c) 1 else return onClosed()
    println("if-body $x")
    return onClosed()
}

// when-as-value
inline fun <R> whenImpl(k: Int, onClosed: () -> R): R {
    val x: Int = when (k) {
        0 -> 10
        else -> return onClosed()
    }
    println("when-body $x")
    return onClosed()
}

// EXPRESSION-body form: kotc emits ONE tail `{k:return, value: elvis{…, returnExpr}}` — the returnExpr is NESTED
// inside the outer tail return's value, so routing must descend into the return's value (else a raw caller-frame ret).
inline fun exprBodyImpl(input: Int?, onClosed: () -> Int): Int = input ?: return onClosed()

fun main() {
    println(elvisImpl(null) { 5 })     // early (expr-position return): onClosed -> 5
    println(elvisImpl("hi") { 6 })     // fall-through: elvis-body hi, then 6
    println(ifImpl(false) { 7 })       // early: onClosed -> 7
    println(ifImpl(true) { 8 })        // fall-through: if-body 1, then 8
    println(whenImpl(1) { 9 })         // early: onClosed -> 9
    println(whenImpl(0) { 11 })        // fall-through: when-body 10, then 11
    println(exprBodyImpl(null) { 12 }) // nested-in-tail-return early: onClosed -> 12
    println(exprBodyImpl(4) { 0 })     // fall-through: input -> 4
}
