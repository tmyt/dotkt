package roundtrip.textbounds

fun charSlice(value: CharArray, start: Int, end: Int): String = value.concatToString(start, end)
fun textSlice(value: String, start: Int, end: Int): CharArray = value.toCharArray(start, end)
fun copyText(value: String, destination: CharArray, offset: Int, start: Int, end: Int): CharArray = value.toCharArray(destination, offset, start, end)
fun encodeText(value: String, start: Int, end: Int): ByteArray = value.encodeToByteArray(start, end, true)
fun decodeText(value: ByteArray, start: Int, end: Int): String = value.decodeToString(start, end, true)
fun savedCopy(): (String, CharArray, Int, Int, Int) -> CharArray = String::toCharArray
inline fun observedCopy(value: String, destination: CharArray, start: Int, end: Int, block: (CharArray) -> CharArray): CharArray =
    block(value.toCharArray(destination, 0, start, end))
