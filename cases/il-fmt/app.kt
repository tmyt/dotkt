// String.format is a thin binding to System.String.Format: the format string is .NET composite ({0:F1}, {0,-4}, {0:x}),
// NOT Java printf. Works for non-literal format strings too (no compile-time translation, no runtime helper).
fun fmtLine(n: Int, pct: Double, label: String): String {
    val tmpl = "{0} items, {1:F1}% ({2})"          // a non-literal (variable) format string
    return String.format(tmpl, n, pct, label)
}

fun main() {
    println(fmtLine(42, 87.5, "ok"))               // 42 items, 87.5% (ok)
    println(String.format("{0:D5}-{1:x}", 7, 255)) // 00007-ff
    for (p in listOf("a", "bb")) println(String.format("[{0,-4}]", p))  // [a   ] / [bb  ]
}
