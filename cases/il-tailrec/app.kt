// COV5 (kcc review §2B): DEEP `tailrec` is now tail-call-optimized (§2b deviation CLOSED, 2026-07-06).
//
// kotc rewrites a self-tail-call in a `tailrec` fn into a back-jump to the method entry (reassign the
// parameters to the call's args, then `goto` the loop head — Kotlin/JVM's own `tailrec` lowering, which our
// pipeline otherwise skips because Fir2Ir runs straight into our backend with no JVM lowerings). So deep tail
// recursion that used to overflow the CLR stack (`sumTo(1_000_000)` = a million real frames) now runs in
// constant stack and matches kotlinc/JVM. Wired into verify-il AND the JVM-oracle differential (PURE).

tailrec fun sumTo(n: Int, acc: Long): Long =            // classic accumulator tailrec
    if (n == 0) acc else sumTo(n - 1, acc + n)

tailrec fun countdown(n: Int): Int =                    // single-arg tailrec
    if (n <= 0) 0 else countdown(n - 1)

tailrec fun gcd(a: Long, b: Long): Long = when {        // multi-branch `when` tail position + swap-style
    b == 0L -> a                                        // arg dependency (temp-first reassign must not
    else -> gcd(b, a % b)                               // corrupt `a`/`b`)
}

tailrec fun Int.countDownExt(acc: Int): Int =           // extension-receiver tailrec (`__self` reassigned)
    if (this <= 0) acc else (this - 1).countDownExt(acc + 1)

class Adder(val step: Int) {                            // member tailrec (dispatch `this` stays the same)
    tailrec fun run(n: Int, acc: Long): Long =
        if (n == 0) acc else run(n - 1, acc + step)
}

fun main() {
    println(sumTo(1_000_000, 0L))                       // 500000500000
    println(countdown(1_000_000))                       // 0
    println(gcd(6_000_000_042L, 4_000_000_028L))        // 2000000014
    println(1_000_000.countDownExt(0))                  // 1000000
    println(Adder(2).run(1_000_000, 0L))                // 2000000
}
