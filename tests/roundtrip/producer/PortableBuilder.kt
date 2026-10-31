package roundtrip.portablebuilder

fun reverseBuilder(builder: StringBuilder): StringBuilder = builder.reverse()
fun replaceBuilder(builder: StringBuilder, start: Int, end: Int, value: String): StringBuilder = builder.setRange(start, end, value)
fun deleteBuilder(builder: StringBuilder, start: Int, end: Int): StringBuilder = builder.deleteRange(start, end)
fun savedBuilderReverse(): (StringBuilder) -> StringBuilder = StringBuilder::reverse
fun savedBuilderAppend(): (StringBuilder, Boolean) -> StringBuilder = StringBuilder::append
fun copyBuilder(builder: StringBuilder): CharArray = CharArray(builder.length).also { builder.toCharArray(it) }
inline fun appendBuilderByte(builder: StringBuilder, value: Byte): StringBuilder = builder.append(value)
