package roundtrip.enumparsename

enum class ParseMode { FIRST, SECOND }
fun producerParse(name: String): ParseMode = ParseMode.valueOf(name)
inline fun <reified E : Enum<E>> producerReifiedParse(name: String): E = enumValueOf<E>(name)
