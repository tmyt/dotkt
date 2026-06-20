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
}
