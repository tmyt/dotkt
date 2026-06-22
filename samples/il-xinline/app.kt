// crossinline: an inline lambda parameter invoked from INSIDE a nested lambda (a deferred context) — which is
// exactly what `crossinline` permits. Such a param can't be spliced; it's bound to a real delegate local and the
// nested lambda captures it via the normal closure machinery.
inline fun twice(crossinline block: () -> Unit) {
    val r = { block() }      // nested lambda calls block -> block must be crossinline
    r(); r()
}
inline fun wrap(crossinline f: () -> Int): Int {
    val g = { f() + 1 }      // nested lambda + value return
    return g()
}
// mixed: a normal (splice-able) lambda alongside a crossinline one in the same inline fun.
inline fun mixed(direct: () -> Int, crossinline deferred: () -> Int): Int {
    val g = { deferred() }   // crossinline -> delegate local
    return direct() + g()    // direct -> spliced
}
fun main() {
    var n = 0
    twice { n += 10 }                 // crossinline lambda mutating a captured var
    println(n)                        // 20
    println(wrap { 41 })              // 42
    println(mixed({ 5 }, { 100 }))    // 105
}
