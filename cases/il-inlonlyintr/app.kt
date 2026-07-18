// #40 (guard): a CROSS-MODULE @kotlin.internal.InlineOnly + @ClrIntrinsic stdlib function keeps its
// @ClrIntrinsic binding across the assembly boundary. kotc carries the annotation as OPAQUE metadata on
// the ref.dll declaration (attrsJson is unconditional — it is NOT dropped for @InlineOnly), and bir2cir's
// MemberCallSubstitution reads it off the ref.dll to substitute the plain Kotlin call to the bound BCL
// member. `sb[0]='X'` is StringBuilder.set (@InlineOnly @ClrIntrinsic("set_Chars")) — the canonical case
// from the issue; the Char predicates are @InlineOnly @ClrIntrinsic("System.Char.Is*") siblings.
fun main() {
    val sb = StringBuilder("abc")
    sb[0] = 'X'                       // StringBuilder.set  -> System.Text.StringBuilder.set_Chars
    println(sb.toString())            // Xbc
    println('a'.isLetter())           // True
    println('5'.isDigit())            // True
    println('A'.isLetterOrDigit())    // True
}
