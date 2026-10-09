@file:Suppress("UNCHECKED_CAST")
package roundtrip.constructorcarrier

class Storage<T>(val value: T)
fun <T> store(value: T): Storage<T> = Storage(value)

class Pairing<A, B>(val first: A, val second: B)
fun <A, B> pair(first: A, second: B): Pairing<A, B> = Pairing(first, second)

class Token<T>(val tag: String)
private val tokenSentinel = Token<Any?>("sentinel")
class ImportedBuffer<E> {
    val storage: Storage<Token<E>> = store(tokenSentinel as Token<E>)
}
fun tokenSentinelObject(): Any = tokenSentinel
