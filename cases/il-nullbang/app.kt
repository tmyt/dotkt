// #56: `!!` (not-null assertion) on a value-type nullable (`Int?` = Nullable<T>) must UNWRAP via
// HasValue/Value and throw NPE on null. A bare pass-through left a Nullable<T> struct where the use
// site wants the bare value (InvalidProgram on `n!! + 1` / garbage on `n!!.toLong()`) and never threw.
fun main() {
    val n: Int? = 5
    println(n!!)            // 5
    println(n!! + 1)        // 6
    println(n!!.toLong())   // 5

    val z: Int? = null
    try { println(z!!) } catch (e: NullPointerException) { println("npe") }

    val l: Long? = 7L
    println(l!! + 3L)       // 10

    val d: Double? = 3.5
    println(d!! + 0.25)     // 3.75

    val b: Byte? = 9
    println(b!!.toInt())    // 9
}
