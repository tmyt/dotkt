package roundtrip.numbers

open class LibraryNumber(private val value: Int) : Number(), Comparable<Number> {
    override fun toByte(): Byte = value.toByte()
    override fun toShort(): Short = value.toShort()
    override fun toInt(): Int = value
    override fun toLong(): Long = value.toLong()
    override fun toFloat(): Float = value.toFloat()
    override fun toDouble(): Double = value.toDouble()
    override fun compareTo(other: Number): Int = toInt().compareTo(other.toInt())
}

class NumberSlots(var value: Number, val nullable: Number?) {
    fun read(): Number = value
}
class NumberBound<T : Number>(val value: T) { fun read(): Int = value.toInt() }
class NumberOrder : Comparable<Number> { override fun compareTo(other: Number): Int = other.toInt() }
fun numberValue(value: Number): Int = value.toInt()
fun numberChoice(value: Number): String = "number"
fun numberChoice(value: Any): String = "any"
fun <T : Number> boundedNumber(value: T): Int = value.toInt()
@Suppress("UNCHECKED_CAST")
fun <T : Number> numberCast(value: Any?): T = value as T
fun numberArray(values: Array<Number>): Array<Number> = values
inline fun <reified T> isNumberType(value: Any?): Boolean = value is T
