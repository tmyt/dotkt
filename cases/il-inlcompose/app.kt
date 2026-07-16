// F3 (#62) — TRANSITIVE forwarding of an inline PARAM through a user top-level inline fn. `outer` forwards its
// inline lambda param `b` to `inner` (`inline fun outer(b)=inner(b)`), which invokes it at `b()`. Because the
// caller's lambda carries a NON-LOCAL return (`return 99` targets compute()), BOTH `outer` and `inner` must
// SPLICE, and the forward chain must carry the caller's carrier through two levels of user top-level inline.
// Before F3: `outer` emitted a plain call — the forwarded `IrGetValue` of its own inline param never tripped the
// splice trigger (`hasLambdaArg` fired only for a literal lambda) → the escaping return fell to the D3-remainder
// fail-loud. After F3: the producer widens the trigger to the forwarded-inline-param shape and the consumer
// forwards the carrier through the nested `callInline`.
inline fun inner(b: () -> Int): Int = b() + 1
inline fun outer(b: () -> Int): Int = inner(b)

fun compute(cond: Boolean): Int {
    val r = outer {
        if (cond) return 99
        10
    }
    return r
}

fun main() {
    println(compute(false))  // inner: b()+1 = 10+1 = 11
    println(compute(true))   // non-local return 99 from the escaping lambda
}
