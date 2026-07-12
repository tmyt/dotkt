// #56: `!!` (not-null assertion) on a value-type nullable (`Int?` = Nullable<T>) must UNWRAP via
// HasValue/Value and throw NPE on null. A bare pass-through left a Nullable<T> struct where the use
// site wants the bare value (InvalidProgram on `n!! + 1` / garbage on `n!!.toLong()`) and never threw.
// #115: `!!` on a REFERENCE nullable (`String?`) must ALSO throw NullPointerException EAGERLY when the
// operand is null — regardless of how the result is used. A bare pass-through only surfaced a later
// NullReferenceException at a deref (wrong type + site) and NEVER threw for a stored/discarded `x!!`.
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

    // #118: `!!` on an UNSIGNED value-type nullable (`UInt?` = Nullable<uint>) must UNWRAP .Value like the
    // signed primitives above — a bare pass-through left a Nullable<uint> struct at the use site (InvalidProgram).
    val u: UInt? = 5u
    println(u!! + 1u)       // 6
    val ub: UByte? = 9u
    println(ub!!.toInt())   // 9

    val uz: UInt? = null
    try { println(uz!!) } catch (e: NullPointerException) { println("npe-u") }

    // #118: extending the value-nullable routing to unsigned ALSO fixes SAFE_CALL / ELVIS on an unsigned
    // nullable (same Nullable<uint> HasValue/Value unwrap) — a raw-struct splice there was the same bug class.
    val us: UInt? = 5u
    println(us?.toInt())        // 5
    val un: UInt? = null
    println(un?.toInt())        // null   (SAFE_CALL yields null when receiver is null)
    println((us ?: 0u) + 1u)    // 6      (ELVIS present -> unwrapped value)
    println((un ?: 9u).toInt()) // 9      (ELVIS fallback)

    // #115 reference-type `!!`.
    val ok: String? = "hi"
    println(ok!!)           // hi   (non-null reference yields the value)
    println(ok!!.length)    // 2    (receiver-position `!!` still yields the value)

    val s: String? = null
    try { s!!; println("no-throw") } catch (e: NullPointerException) { println("npe-discard") }  // discarded stmt still throws EAGERLY

    val s2: String? = null
    try { val y: String = s2!!; println(y) } catch (e: NullPointerException) { println("npe-store") }  // stored `val y = x!!` still throws EAGERLY
}
