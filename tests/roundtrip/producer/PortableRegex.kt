package roundtrip.portableregex

fun splitLazily(pattern: Regex, input: String, limit: Int): Sequence<String> =
    pattern.splitToSequence(input, limit)

fun matchesFrom(input: String, startIndex: Int): Sequence<MatchResult> =
    Regex("(?<word>a)(?<tail>b)?").findAll(input, startIndex)

fun capturedMatch(input: String): MatchResult = Regex("(?<word>a)(?<tail>b)?").find(input)!!

fun capturedGroup(match: MatchResult, name: String): MatchGroup? = match.groups[name]

fun replaceFromLibrary(regex: Regex, input: String, replacement: String): String =
    regex.replace(input, replacement)

inline fun observeCapturedGroups(input: String, block: (MatchResult) -> String): String = block(capturedMatch(input))
