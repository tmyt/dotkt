// TASK #141: a value-returning infinite loop (`while(true){ … return x }`) — atomicfu loop/getAndUpdate,
// a NativeMutex inner loop — CFG-lowers to a `brfalse end` on a constant-true condition, so the loop-exit
// label is STATICALLY reachable and the method's trailing `ret` sits there with an empty stack in a
// non-void method -> ilverify ReturnMissing (the JIT runs it fine; the exit is never taken). ilemit now
// appends `default(ret); ret` so the unreachable terminator is stack-valid. Covers the value-type return
// (ldloca/initobj) via Int and the reference return (ldnull) via String.
private var n = 0

fun nextInt(): Int {
    while (true) {
        n++
        if (n >= 3) return n * 10
    }
}

fun firstEven(): String {
    while (true) {
        n++
        if (n % 2 == 0) return "ok$n"
    }
}

fun main() {
    println(nextInt())    // 30
    println(firstEven())  // ok4
}
