package roundtrip.genericreferenceequality

fun <T> nullableFirst(a: T?, b: T): Boolean = a === b
fun <T> nullableLast(a: T, b: T?): Boolean = a === b
fun <T> different(a: T?, b: T): Boolean = a !== b
fun <T> rawSame(a: T, b: T): Boolean = a === b
inline fun <T> inlineSame(a: T?, b: T): Boolean = a === b
class Holder<T>(val nullable: T?, val value: T) {
    fun same(): Boolean = nullable === value
    fun reversed(): Boolean = value === nullable
}
