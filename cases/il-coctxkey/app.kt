// GitHub #2: subclassing AbstractCoroutineContextElement forces the loader to realize the base type, whose
// `get_key(): Key<*>` was lowered by kotc to `Key<object>` (its self-ref-bounded `Key<E : Element>` star
// projection). bir2cir's StarProjectionBoundLowering repoints `Key<object>` -> `Key<Element>` so the
// methodimpl signature matches its interface declaration. (Full run-green ALSO needs ilemit to forward the
// inherited GENERIC default-interface-method `get<E : Element>(key: Key<E>)` without erasing E to object.)
import kotlin.coroutines.AbstractCoroutineContextElement
import kotlin.coroutines.CoroutineContext

class MyElem : AbstractCoroutineContextElement(Key) {
    companion object Key : CoroutineContext.Key<MyElem>
}

fun main() {
    val e = MyElem()
    println(e.key === MyElem.Key)   // True
    println(e[MyElem.Key] === e)    // True (get<E : Element>)
}
