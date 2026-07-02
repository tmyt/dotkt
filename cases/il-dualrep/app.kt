// Dual-representation rule (docs/dotkt-semantics.md): `import System.Text.StringBuilder` (the raw .NET view,
// injected by facadegen) and the stdlib's default-imported `kotlin.text.StringBuilder` (@ClrTypeAlias-bound,
// Kotlin-flavored members) are TWO DISTINCT frontend types over the SAME CLR runtime type. They coexist in one
// program; mixing identities is a frontend type error; an explicit cast is the escape hatch (same CLR type, so
// the runtime checkcast always succeeds).
import System.Text.StringBuilder

fun useKt(sb: kotlin.text.StringBuilder): String = sb.toString()

fun main() {
    // The imported .NET view: raw BCL surface (Append/Length/ToString).
    val net = StringBuilder()
    net.Append("net")
    println(net.ToString())
    println(net.Length)
    // The stdlib view, in the same program: buildString works on kotlin.text.StringBuilder.
    val s = buildString { append("kt") }
    println(s)
    // Escape hatch: an explicit cast crosses the views (both erase to System.Text.StringBuilder).
    @Suppress("CAST_NEVER_SUCCEEDS")
    val kt = net as kotlin.text.StringBuilder
    println(useKt(kt))
}
