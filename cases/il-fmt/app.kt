// Non-literal String.format -> the DotKt.Runtime helper (DotKt.Fmt.format does the printf->composite conversion at
// runtime). A LITERAL format is still converted at compile time (no runtime dependency for the common case).
fun fmtLine(n: Int, pct: Double, label: String): String {
    val tmpl = "%d items, %.1f%% (%s)"            // non-literal (a variable) -> DotKt.Runtime
    return String.format(tmpl, n, pct, label)
}

fun main() {
    println(fmtLine(42, 87.5, "ok"))              // 42 items, 87.5% (ok)
    println(String.format("%05d-%x", 7, 255))     // literal -> compile-time: 00007-ff
    for (p in listOf("a", "bb")) println(String.format("[%-4s]", p))  // [a   ] / [bb  ]
}
