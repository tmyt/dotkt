// CharSequence.subSequence(start, end) -> String.Substring(start, end-start), with `start` evaluated ONCE even
// when it is side-effecting (bundle-6 BUG-4: the length `end - start` used to re-run `start`).
var calls = 0
fun start(): Int { calls++; return 1 }
fun main() {
    val cs: CharSequence = "hello"
    println(cs.subSequence(start(), 4))   // ell
    println(calls)                        // 1  (start() ran exactly once)
    println(cs.subSequence(0, 3))         // hel
    println(cs.subSequence(2, 5))         // llo
}
