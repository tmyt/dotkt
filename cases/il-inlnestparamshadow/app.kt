// F2 (#61) — a nested inlineLambda param that SHADOWS an outer inline callee's value param BY NAME.
// `outer` is spliced as a callInline (it carries a lambda `f`). Its body nests a call to `inner` whose
// lambda arg `{ x -> x * 10 }` declares a param `x` that collides with outer's own value param `x`.
// When outer is spliced, RewriteLocalRefs binds outer's `x` -> a fresh temp; WITHOUT the inlineLambda
// scope boundary it descends into the nested carrier and rebinds the inner lambda's `x` READ to outer's
// temp — a SILENT miscompile (all Int, types match). The inner `x` must read INNER's value (inner's `a`),
// never outer's. (The outer `x` used as `inner(x + 1)`'s arg is OUTSIDE the carrier and IS bound correctly.)
inline fun inner(a: Int, g: (Int) -> Int): Int = g(a)
inline fun outer(x: Int, f: (Int) -> Int): Int = f(inner(x + 1) { x -> x * 10 })

fun main() {
    // outer(5) { it + 1000 }:  inner(6) { x -> x*10 } = 60 ; f(60) = 1060
    // BUG (pre-fix): inner's `x` rebound to outer's temp (5) -> 5*10 = 50 ; f(50) = 1050
    println(outer(5) { it + 1000 })   // 1060
}
