package roundtrip.portablestringcomparison

fun equalText(a: String?, b: String?, ignoreCase: Boolean): Boolean = a.equals(b, ignoreCase)
fun compareText(a: String, b: String, ignoreCase: Boolean): Int = a.compareTo(b, ignoreCase)
fun textPrefix(a: String, part: String, offset: Int): Boolean = a.startsWith(part, offset, true)
fun textSuffix(a: String, part: String): Boolean = a.endsWith(part, true)
fun savedTextComparison(): (String, String, Boolean) -> Int = String::compareTo
fun savedTextEquality(): (String?, String?, Boolean) -> Boolean = String?::equals
fun textOrder(): Comparator<String> = String.CASE_INSENSITIVE_ORDER
inline fun observedTextComparison(a: String, b: String, block: (Int) -> Int): Int = block(a.compareTo(b, true))
