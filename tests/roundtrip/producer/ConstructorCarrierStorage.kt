@file:Suppress("UNCHECKED_CAST")
package roundtrip.constructorcarrier

interface ValueEcho {
    fun <R> echo(value: R): R
}

interface StoredView<T> { val value: T }
class Storage<T>(override val value: T) : StoredView<T>
fun <T> store(value: T): Storage<T> = Storage(value)

interface BoundCounter<T> {
    fun increment()
    fun count(): Int
}
fun boundCounterCount(value: BoundCounter<*>): Int = value.count()

class Pairing<A, B>(val first: A, val second: B)
fun <A, B> pair(first: A, second: B): Pairing<A, B> = Pairing(first, second)

class Token<T>(val tag: String)
private val tokenSentinel = Token<Any?>("sentinel")
class ImportedBuffer<E> {
    val storage: Storage<Token<E>> = store(tokenSentinel as Token<E>)
}
class MutableImportedBuffer<E> {
    var storage: Storage<Token<E>> = store(tokenSentinel as Token<E>)
    fun reset() { storage = store(tokenSentinel as Token<E>) }
}
fun tokenSentinelObject(): Any = tokenSentinel
