// Top-level `lateinit var` of a reference type (#104): its static field has NO initializer, so bir2cir emits
// `"init": null` — ilemit must NOT feed that null to the .cctor store path (it would crash EmitNullableCoerced).
// The field stays default (null) until assigned; a read before assignment goes through the `lateinitGet` check
// and throws. This sample exercises both: the throw-before-init and the normal assign/read.
lateinit var s: String

fun main() {
    try { println(s.length) } catch (e: Exception) { println("caught: uninitialized") }
    s = "hello"
    println(s)
    println(s.length)
}
