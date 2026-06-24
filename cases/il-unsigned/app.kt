// Unsigned types (Kotlin inline classes) -> native CLR unsigned primitives (UInt32/UInt64/Byte/UInt16).
fun main() {
    val a: UInt = 4000000000u          // > Int.MAX_VALUE
    val b: UInt = 100u
    println(a + b)                      // 4000000100  (unsigned add + print)
    println(a.toString())              // 4000000000

    val c: ULong = 18000000000000000000uL
    println(c)                          // 18000000000000000000

    val s: UShort = 60000u
    val by: UByte = 250u
    println(s)                          // 60000
    println(by)                         // 250
}
