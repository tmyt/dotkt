// bir2cir SuspendColdLowering P3: control flow across suspension (if/when/while/for), try/catch
// with the suspension in the try body, and a suspend extension fun. Drained by the synthesized
// plain `main` (sync-completion path; every suspend call here completes synchronously).

suspend fun one(): Int = 1
suspend fun two(): Int = 2

// CF1: an `if` (a `cond` ternary) with a suspend call in each branch.
suspend fun cf1(b: Boolean): Int {
    val x = if (b) one() else two()
    return x + 10
}

// CF2: a `while` loop summing N suspend-call results (loop induction + accumulator cross suspension).
suspend fun cf2(n: Int): Int {
    var acc = 0
    var i = 0
    while (i < n) {
        acc = acc + one()
        i = i + 1
    }
    return acc
}

// CF3: a `when` with a suspension in a branch.
suspend fun cf3(n: Int): Int {
    val x = when (n) {
        0 -> one()
        1 -> two()
        else -> 99
    }
    return x
}

// CF4: a `for (e in xs)` with a suspend call in the body.
suspend fun cf4(xs: List<Int>): Int {
    var acc = 0
    for (e in xs) {
        acc = acc + e + one()
    }
    return acc
}

// EXC1: a suspension in the try BODY; the catch catches a post-resume throw.
suspend fun exc1(fail: Boolean): Int {
    try {
        val x = one()
        if (fail) throw IllegalStateException("after resume")
        return x + 100
    } catch (e: Exception) {
        return -1
    }
}

// EXT1: a suspend extension fun (kotc lowers the receiver to a `__self` param).
suspend fun Int.plusOneS(): Int = this + 1

suspend fun main() {
    println(cf1(true))
    println(cf1(false))
    println(cf2(3))
    println(cf3(0))
    println(cf3(1))
    println(cf3(5))
    println(cf4(listOf(10, 20)))
    println(exc1(false))
    println(exc1(true))
    println(41.plusOneS())
}
