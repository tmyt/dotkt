package roundtrip.signedintegers

fun parsedByte(value: String): Byte = value.toByte()
fun parsedByte(value: String, radix: Int): Byte = value.toByte(radix)
fun parsedShort(value: String): Short = value.toShort()
fun parsedShort(value: String, radix: Int): Short = value.toShort(radix)
fun parsedInt(value: String): Int = value.toInt()
fun parsedInt(value: String, radix: Int): Int = value.toInt(radix)
fun parsedLong(value: String): Long = value.toLong()
fun parsedLong(value: String, radix: Int): Long = value.toLong(radix)
fun savedLongParser(radix: Int): (String) -> Long = { it.toLong(radix) }
