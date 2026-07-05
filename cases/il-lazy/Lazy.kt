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

    // Explicit LazyThreadSafetyMode overloads: SYNCHRONIZED + PUBLICATION -> SynchronizedLazyImpl,
    // NONE -> UnsafeLazyImpl. Each must still memoize (initializer runs exactly once).
    var s = 0
    val sync = lazy(LazyThreadSafetyMode.SYNCHRONIZED) { s++; "sync" }
    println(sync.value)           // sync
    println(sync.value)           // sync
    println(s)                    // 1

    var p = 0
    val pub = lazy(LazyThreadSafetyMode.PUBLICATION) { p++; "pub" }
    println(pub.value)            // pub
    println(p)                    // 1

    var n = 0
    val none = lazy(LazyThreadSafetyMode.NONE) { n++; "none" }
    println(none.value)           // none
    println(n)                    // 1

    // The explicit-lock overload -> SynchronizedLazyImpl(initializer, lock).
    var k = 0
    val guarded = lazy(Any()) { k++; "guarded" }
    println(guarded.isInitialized())  // false
    println(guarded.value)            // guarded
    println(guarded.isInitialized())  // true
    println(k)                        // 1
}
