class Config(val base: Int) {
    // lazy over a reference type, with a side effect to prove single evaluation + memoization.
    val expensive: String by lazy {
        println("computing...")
        "VALUE"
    }
    // lazy over a value type whose initializer captures `this` (reads base) -> closure -> Func<Int>.
    val doubled: Int by lazy { base * 2 }
}

fun main() {
    val c = Config(21)
    println("before")
    println(c.expensive)
    println(c.expensive)
    println(c.doubled)
    println(c.doubled)

    // A directly-held Lazy<T>: isInitialized() flips false -> true across the first `.value`, and the
    // initializer runs exactly once (memoization), proving the pure-Kotlin UnsafeLazyImpl semantics.
    var count = 0
    val lz = lazy { count++; "computed" }
    println(lz.isInitialized())   // false
    println(lz.value)             // computed
    println(lz.value)             // computed (memoized, not recomputed)
    println(lz.isInitialized())   // true
    println(count)                // 1

    // A LOCAL `by lazy` delegate (exercises the local-delegated-property path, distinct from the member one).
    val local: Int by lazy { 7 * 6 }
    println(local)                // 42
    println(local)                // 42
}
