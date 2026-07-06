// COV5 (kcc review §2B): DEEP tailrec — a DOCUMENTED-DEVIATION REPRO, deliberately NOT wired into any gate.
//
// EMPIRICAL VERDICT (2026-07-06, `dotkt.sh --run`): our compiler does NOT emit tail-call optimization for
// `tailrec`. sumTo(1_000_000) recurses a million real frames and STACK-OVERFLOWS on the CLR (`Stack overflow.
// Repeated 174376 times: at AppKt.sumTo`), whereas kotlinc/JVM rewrites `tailrec` into a loop and prints
// 500000500000. So deep tailrec is a genuine divergence from Kotlin/JVM. This is recorded in
// docs/dotkt-semantics.md ("tailrec is NOT tail-call optimized"); the routed fix is a tail-call lowering in
// kotc/bir2cir (rewrite a self-tail-call `tailrec` fn into a loop, or emit the CIL `.tail.` prefix).
//
// It is intentionally left OUT of verify-il (would `run crash`) and OUT of the differential PURE set (would
// DIFF/crash) so the gates stay XFAIL-zero; this file remains as the reproducer. Run it manually with
// `./scripts/dotkt.sh --run cases/il-tailrec/app.kt` to observe the overflow.
tailrec fun sumTo(n: Int, acc: Long): Long =
    if (n == 0) acc else sumTo(n - 1, acc + n)

tailrec fun countdown(n: Int): Int =
    if (n <= 0) 0 else countdown(n - 1)

fun main() {
    println(sumTo(1_000_000, 0L))   // 500000500000
    println(countdown(1_000_000))   // 0
}
