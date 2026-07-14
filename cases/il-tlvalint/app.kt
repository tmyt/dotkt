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

    // #11 WRITE side — the value-type platform slot coerces a nullable/null source down to the bare `int32` setter.
    ti.Value = 5                           // a bare non-null value write works today (non-regression)
    println(ti.Value)                      // 5
    val q: Int? = 7                        // a genuine Kotlin `Int?` = `Nullable<Int32>`, holding a value
    ti.Value = q                           // bir2cir unwraps the Nullable<Int32> to the bare `int32` the slot expects
    println(ti.Value)                      // 7
    // reference twin: a `String?` into a reference slot needs NO value coercion (non-regression).
    val sq: String? = "hi"
    ts.Value = sq
    println(ts.Value)                      // hi
    // (A LITERAL `null` write into a value slot — `ti.Value = null` — is a LOUD bir2cir emit-time error, not a runtime
    //  case; see docs/dotkt-semantics.md §9a-bis. It cannot be a successful-run gate sample, so it is not covered here.)
}
