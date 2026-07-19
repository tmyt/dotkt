// #178: the options-taking Regex constructors Regex(String, RegexOption) / Regex(String, Set<RegexOption>).
// bir2cir (NetInteropBinding) converts the RegexOption / Set<RegexOption> arg to the BCL RegexOptions int bitmask
// (IGNORE_CASE->1 / MULTILINE->2 / DOT_MATCHES_ALL->16 / COMMENTS->32) at the ctor call site.
fun main() {
    // single RegexOption (compile-time enum constant)
    println(Regex("a", RegexOption.IGNORE_CASE).matches("A"))                       // True  (case-insensitive)
    println(Regex("a", RegexOption.IGNORE_CASE).matches("a"))                       // True
    println(Regex("a").matches("A"))                                               // False (control: no option)

    // COMMENTS -> IgnorePatternWhitespace: unescaped whitespace in the pattern is ignored
    println(Regex("a b", setOf(RegexOption.COMMENTS)).matches("ab"))                // True

    // DOT_MATCHES_ALL -> Singleline: '.' matches a newline
    println(Regex("a.b", setOf(RegexOption.DOT_MATCHES_ALL)).matches("a\nb"))       // True
    println(Regex("a.b").matches("a\nb"))                                          // False (control)

    // MULTILINE -> Multiline: '^' matches at a line start (not just string start)
    println(Regex("^b", setOf(RegexOption.MULTILINE)).containsMatchIn("a\nb"))       // True
    println(Regex("^b").containsMatchIn("a\nb"))                                    // False (control)

    // multi-element set (OR of two bits)
    println(Regex("A B", setOf(RegexOption.IGNORE_CASE, RegexOption.COMMENTS)).matches("ab"))  // True

    // runtime-held option (not a compile-time constant) exercises the enumOrdinal path
    val opt = RegexOption.IGNORE_CASE
    println(Regex("x", opt).matches("X"))                                          // True
}
