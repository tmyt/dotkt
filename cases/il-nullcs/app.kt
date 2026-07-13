// #156 — a genuinely-nullable String (`z: String? = null`) passed UNWRAPPED into a `CharSequence?`-receiver slot
// (`isNullOrEmpty`) fails ilverify on the String->dotkt$CharSequence interface assignment (String does not implement
// the synthetic adapter interface) even though it RUNS (a null short-circuits isNullOrEmpty). bir2cir's
// StringCharSequenceBridge now emits a runtime-conditional wrap on the strict nullable-slot path:
// `v == null ? (dotkt$CharSequence)null : new dotkt$StringCharSequence(v)` — ilverify-clean AND null-preserving.
fun pick(n: Int): String? = if (n > 0) "hi" else null
fun main() {
    val z: String? = null
    println(if (z.isNullOrEmpty()) "Z:empty" else "Z:$z")   // z=null  -> Z:empty
    val v: String? = pick(1)
    println(if (v.isNullOrEmpty()) "V:empty" else "V:$v")   // v="hi"  -> V:hi   (adapter wrap; non-stable subject)
    val e: String? = ""
    println(if (e.isNullOrEmpty()) "E:empty" else "E:$e")   // e=""    -> E:empty (adapter, length 0)
}
