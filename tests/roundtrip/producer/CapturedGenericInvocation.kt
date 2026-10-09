@file:Suppress("UNCHECKED_CAST")
package roundtrip.capturedgeneric

import kotlin.coroutines.CoroutineContext

object FirstContextKey : CoroutineContext.Key<FirstContextElement>
class FirstContextElement : CoroutineContext.Element {
    override val key: CoroutineContext.Key<*> get() = FirstContextKey
}
object SecondContextKey : CoroutineContext.Key<SecondContextElement>
class SecondContextElement : CoroutineContext.Element {
    override val key: CoroutineContext.Key<*> get() = SecondContextKey
}

fun readCapturedContext(context: CoroutineContext, element: CoroutineContext.Element): CoroutineContext.Element? = context[element.key]
fun <T> readCapturedContextInMethod(context: CoroutineContext, element: CoroutineContext.Element, token: T): CoroutineContext.Element? {
    check(token != null)
    return context[element.key]
}
class CapturedContextReader<T>(val token: T) {
    fun <R> read(context: CoroutineContext, element: CoroutineContext.Element, other: R): CoroutineContext.Element? {
        check(token != null && other != null)
        return context[element.key]
    }
}

interface Lookup { fun <E : Element> lookup(key: Key<E>): E? }
interface Key<E : Element>
interface Element : Lookup {
    val key: Key<*>
    override fun <E : Element> lookup(key: Key<E>): E? = if (this.key === key) this as E else null
}
object FirstKey : Key<First>
class First : Element { override val key: Key<*> get() = FirstKey }
object OtherKey : Key<Other>
class Other : Element { override val key: Key<*> get() = OtherKey }
fun readCapturedLocal(context: Lookup, element: Element): Element? = context.lookup(element.key)
fun <E : Element> lookupStatic(context: Lookup, key: Key<E>): E? = context.lookup(key)
fun readCapturedStatic(context: Lookup, element: Element): Element? = lookupStatic(context, element.key)

class OverloadedLookup(val context: Lookup) {
    fun <E : Element> lookup(key: Key<E>): E? = context.lookup(key)
    fun <E : Element> lookup(unused: String): E? = error("wrong overload: " + unused)
}
fun readCapturedOverload(context: OverloadedLookup, element: Element): Element? = context.lookup(element.key)

interface PlainKey<T>
object TextKey : PlainKey<String>
fun <T> plainTag(key: PlainKey<T>): String = "plain-key"
fun readPlainKey(key: PlainKey<*>): String = plainTag(key)

class KeyCounter {
    var count = 0
    fun <E : Element> consume(key: Key<E>) { count += 1 }
}
fun consumeCapturedKey(counter: KeyCounter, element: Element) = counter.consume(element.key)

class PrivateKeyReader<T>(val token: T) {
    private fun <E> tag(key: PlainKey<E>, other: T): String {
        check(token == other)
        return "private-key"
    }
    fun read(key: PlainKey<*>): String = tag(key, token)
}
fun <T> nullableKeyTag(key: PlainKey<T>, count: Int?): Int = count ?: 0
fun readNullableKey(key: PlainKey<*>, count: Int?): Int = nullableKeyTag(key, count)
object IntegerKey : PlainKey<Int>
fun <A, B> twoKeyTag(first: PlainKey<A>, second: PlainKey<B>): String = "two-keys"
fun readTwoKeys(first: PlainKey<*>, second: PlainKey<*>): String = twoKeyTag(first, second)

open class BaseKeyReader {
    open fun <T> read(key: PlainKey<T>): String = "base-key"
}
class DerivedKeyReader : BaseKeyReader() {
    override fun <T> read(key: PlainKey<T>): String = "derived-key"
    fun readBase(key: PlainKey<*>): String = super.read(key)
}

interface VariantKey<in A, B>
object AnyTextKey : VariantKey<Any, String>
fun <A, B> variantKeyTag(key: VariantKey<A, B>, values: MutableList<A>): Int = values.size
fun readVariantKey(key: VariantKey<String, *>, values: MutableList<String>): Int = variantKeyTag(key, values)
fun <E : Element> echoCapturedKey(key: Key<E>): Key<E> = key
fun readCapturedKeyResult(key: Key<*>): Key<*> = echoCapturedKey(key)
