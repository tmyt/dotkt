import System.Threading.ThreadLocal

// #8: a facadegen-injected `[MaybeNull]` VALUE-type getter (`ThreadLocal<Int>.Value`) is an oblivious platform type
// `Int!`, NOT a genuine `Int?`. It must lower to a BARE `int32` (default `0` when unset), never `Nullable<Int32>`.
// The reference twin (`ThreadLocal<String>.Value`, #143) proves non-regression: a reference oblivious stays a bare
// nullable-in-IL reference, so its `== null` is a REAL runtime check.
fun main() {
    val ti = ThreadLocal<Int>()
    val n: Int = ti.Value                 // value-type platform default -> 0 (a Nullable<Int32> would read garbage / fail)
    println(n)                             // 0
    println(ti.Value == null)              // False — a bare value type, the `== null` is statically false
    val e: Int = ti.Value ?: 99            // elvis over a non-null bare value -> the value itself
    println(e)                             // 0

    // #143 reference-oblivious twin (non-regression): a reference platform type keeps a real null check.
    val ts = ThreadLocal<String>()
    println(ts.Value == null)              // True — reference oblivious, unset -> null
}
