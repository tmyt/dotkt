// #75 S4a §8.6 — payload FORWARDING (§4.4i) + an ESCAPING non-local return. `filter`'s raw payload is
// `return filterTo(ArrayList(), predicate)` — a plain owner-less `callStatic` to the inline `filterTo`, with
// `predicate` forwarded BY NAME (not a direct invoke inside filter's body). Because the caller's lambda escapes
// (`return "neg"` targets evens()), `filter` splices, and the engine's §4.4(i) forwarding CONVERTS the nested
// `callStatic filterTo` into a `callInline` carrying the caller's lambda, which the fixpoint then splices where
// filterTo invokes `predicate(element)` — an `if`-condition (a clean-stack position, so the non-local return is
// valid CIL). Proves the forward wiring end to end.
fun evens(xs: List<Int>): String {
    return xs.filter {
        if (it < 0) return "neg"
        it % 2 == 0
    }.joinToString(",")
}
fun main() {
    println(evens(listOf(1, 2, 3, 4)))   // 2,4
    println(evens(listOf(1, -5, 4)))     // neg
}
