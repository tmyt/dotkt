@file:Suppress("DEPRECATION")

package roundtrip.portabletext

fun replaceText(value: String, old: String, new: String, ignoreCase: Boolean): String = value.replace(old, new, ignoreCase)
fun replaceFirstChar(value: String, old: Char, new: Char): String = value.replaceFirst(old, new, true)
fun repeatedText(value: CharSequence, count: Int): String = value.repeat(count)
fun capitalizedText(value: String): String = value.capitalize()
fun controlCharacter(value: Char): Boolean = value.isISOControl()
fun savedReplacement(): (String, String, String, Boolean) -> String = String::replace
inline fun observeReplacement(value: String, old: String, new: String, block: (String) -> String): String =
    block(value.replace(old, new))
