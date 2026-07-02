// CharSequence is `string` on the CLR (docs/design-charsequence-clr-string.md, the 3-point model).
// In an app with NO user `class : CharSequence`, a CharSequence-typed param / return / local lowers to
// System.String: member reads (length / get / subSequence) resolve to String.Length / get_Chars / Substring,
// a String flows in directly, and a non-String CharSequence (a StringBuilder) is snapshot with an implicit
// `.toString()` at the boundary (point ②). No synthetic <>dotkt_CharSequence is needed here.
fun len(cs: CharSequence): Int = cs.length
fun at(cs: CharSequence, i: Int): Char = cs[i]
fun tail(cs: CharSequence, from: Int): CharSequence = cs.subSequence(from, cs.length)
fun has(cs: CharSequence, sub: String): Boolean = cs.contains(sub)   // now-string cs -> stdlib CharSequence-ext

fun main() {
    println(len("hello"))          // 5    String -> string param
    println(at("hello", 1))        // e    get -> System.String.get_Chars
    println(tail("hello", 2))      // llo  subSequence -> Substring; CharSequence return = string
    val sb = StringBuilder()
    sb.append("world")
    println(len(sb))               // 5    StringBuilder -> implicit .toString() snapshot
    val cs: CharSequence = "abc"   // CharSequence local -> string
    println(len(cs))               // 3
    println(cs.length)             // 3    member read on a now-string local
    println(has("hello", "ell"))   // True  String -> string param -> stdlib ext (bridge composes)
    println(has(sb, "orl"))        // True  StringBuilder -> toString snapshot -> stdlib ext
}
