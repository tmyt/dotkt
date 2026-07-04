// kotlin.Result / runCatching -> a synthetic generic Result<T> (value/failure/isSuccess fields). runCatching
// wraps a try/catch; accessors (getOrNull/getOrThrow/getOrDefault/exceptionOrNull/isSuccess/isFailure) inline.
fun risky(n: Int): Int { if (n < 0) throw IllegalStateException("neg $n"); return n * 2 }
fun greet(ok: Boolean): String { if (!ok) throw RuntimeException("bad"); return "hi" }

fun main() {
    val r = runCatching { risky(5) }
    println(r.isSuccess)             // true
    println(r.getOrNull())          // 10
    println(r.getOrThrow())         // 10

    val r2 = runCatching { risky(-1) }
    println(r2.isFailure)           // true
    println(r2.getOrNull())         // null  (value-type Result<Int> failure -> "null")
    println(r2.getOrDefault(-99))   // -99
    println(r2.exceptionOrNull()?.message)  // neg -1  (Throwable.message -> Exception.Message)

    val rs = runCatching { greet(false) }
    println(rs.getOrNull())         // null  (ref-type Result<String> failure)
    println(rs.getOrDefault("fb"))  // fb
}
