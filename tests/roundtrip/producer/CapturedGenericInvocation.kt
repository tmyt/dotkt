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
