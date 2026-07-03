// Number-conversion correctness: base-10 parses throw a real Kotlin NumberFormatException (catchable, and as its
// IllegalArgumentException supertype), and float/double parsing is culture-INVARIANT (a comma is not a group separator).
fun main() {
    println("42".toInt())                                                 // 42
    println("-7".toLong())                                                // -7
    println("100".toByte())                                               // 100
    try { "abc".toInt(); println("no") } catch (e: NumberFormatException) { println("nfe") }
    try { "x".toInt();   println("no") } catch (e: IllegalArgumentException) { println("iae") }
    println("3.14".toDouble())                                            // 3.14
    println("2.5".toFloat())                                              // 2.5
    try { "3,14".toDouble(); println("no") } catch (e: NumberFormatException) { println("comma") }
    try { "zzz".toDouble(); println("no") } catch (e: NumberFormatException) { println("nfd") }
}
