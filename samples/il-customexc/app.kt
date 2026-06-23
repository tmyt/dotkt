// User-defined exception extending Kotlin's Exception/RuntimeException -> a CLR class : System.Exception. The base
// ctor chains to System.Exception(string), `.message` -> .Message, and it's catchable by base type. (Very common.)
class AppErr(val code: Int) : Exception("error " + code)
class RtErr(m: String) : RuntimeException(m)
fun risky(n: Int): Int { if (n < 0) throw AppErr(n); return n * 2 }
fun main() {
    try { risky(-5) } catch (e: AppErr) { println(e.message); println("code=" + e.code) }   // error -5 / code=-5
    try { throw RtErr("boom") } catch (e: Exception) { println("caught:" + e.message) }       // caught:boom
    println(risky(21))                                                                         // 42
}
