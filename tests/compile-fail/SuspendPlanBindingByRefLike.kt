// A byref-like (`ref struct`) value passed to a call that FILLS a default, where a LATER value of the same call
// suspends. Kotlin evaluates the argument before the suspension and the call reads it after, so the value has to
// survive the resume — and the state machine cannot hold a `ref struct` in an instance field. The refusal names the
// value by its SOURCE ROLE ("the argument 's' of 'cfbLen'"), which the call-evaluation plan carries onto the local
// (docs/bir-cir-spec.md §2.7); without it the message could only offer the minted binding name.
//
// The contrast is tests/interop/consumer/fixtures/ByRefLikeSingleEvalTests.kt: exactly this shape with the
// suspension AFTER the call compiles and runs, because the byref-like value is dead by then and stays a local.
import System.Span

suspend fun cfbRelay(n: Int): Int = n + 1

// `b` reads `a`, so `a` has two readers and must be evaluated into a local — which forces every earlier value of the
// call, the byref-like `s` included, to be evaluated ahead of the call rather than reordered behind it.
fun cfbLen(s: Span<Int>, a: Int, b: Int = a * 10): Int = s.Length + a + b

suspend fun cfbSpanArgumentAcrossSuspension(): Int =
    cfbLen(Span<Int>(arrayOf(1, 2, 3)), cfbRelay(4))

suspend fun main() {
    println(cfbSpanArgumentAcrossSuspension())
}
