package roundtrip.unicodedigits

fun decimalDigit(value: Char): Int = value.digitToInt()
fun radixDigit(value: Char, radix: Int): Int? = value.digitToIntOrNull(radix)
fun signedNumber(value: String): Long = value.toLong()
fun unsignedNumber(value: String, radix: Int): ULong = value.toULong(radix)
fun savedDigit(): (Char, Int) -> Int? = Char::digitToIntOrNull
fun nativeCharacter(value: String): Char = System.Char.Parse(value)
fun nativeDigitValue(value: Char): Double = System.Char.GetNumericValue(value)
inline fun observedDigit(value: Char, radix: Int, block: (Int?) -> Int?): Int? = block(value.digitToIntOrNull(radix))
