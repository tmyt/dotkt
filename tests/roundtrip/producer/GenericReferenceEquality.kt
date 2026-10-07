package roundtrip.genericreferenceequality

fun <T> nullableFirst(a: T?, b: T): Boolean = a === b
fun <T> nullableLast(a: T, b: T?): Boolean = a === b
fun <T> different(a: T?, b: T): Boolean = a !== b
fun <T> rawSame(a: T, b: T): Boolean = a === b
fun <T> effectsSame(left: () -> T, right: () -> T): Boolean = left() === right()
inline fun <T> splicedRawSame(a: T, b: T, before: () -> Unit): Boolean {
    before()
    return a === b
}
inline fun <T> splicedNullableSame(a: T?, b: T, before: () -> Unit): Boolean {
    before()
    return a === b
}
@Suppress("UNCHECKED_CAST")
fun <T> uncheckedSame(a: Any?, b: Any?): Boolean = (a as T) === (b as T)
class IdentityHolder<T> {
    fun same(a: T, b: T): Boolean = a === b
}
enum class IdentityColor { RED, BLUE }
fun <T> localCondition(a: T?, b: T): Boolean {
    val left: T? = a
    val right: T = b
    return if (left === right) true else false
}
private fun <T> nullableResult(value: T?): T? = value
private fun <T> valueResult(value: T): T = value
fun <T> callResults(a: T?, b: T): Boolean = nullableResult<T>(a) === valueResult<T>(b)
fun <T> projectedResult(a: Result<T>, b: T): Boolean = a.getOrThrow() === b
inline fun <T> inlineSame(a: T?, b: T): Boolean = a === b
inline fun <T> splicedSame(a: T?, b: T, hook: () -> Unit): Boolean {
    hook()
    return a === b
}
class Holder<T>(val nullable: T?, val value: T) {
    fun same(): Boolean = nullable === value
    fun reversed(): Boolean = value === nullable
}
