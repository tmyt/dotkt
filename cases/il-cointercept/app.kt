// GitHub #2: `override val key: CoroutineContext.Key<*>` on a ContinuationInterceptor impl was lowered to
// `Key<object>` (the self-ref-bounded `Key<E : Element>` star projection), which VIOLATES `E : Element` at
// the CLR generic instantiation ("GenericArguments[0] System.Object violates the constraint of type E").
// bir2cir's StarProjectionBoundLowering repoints it to `Key<Element>`. (Full run-green ALSO needs ilemit to
// implement the inherited GENERIC default-interface-method `get<E : Element>(key: Key<E>)` on the impl.)
import kotlin.coroutines.ContinuationInterceptor
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.Continuation

class MyInterceptor : ContinuationInterceptor {
    override val key: CoroutineContext.Key<*> get() = ContinuationInterceptor
    override fun <T> interceptContinuation(continuation: Continuation<T>): Continuation<T> = continuation
}

fun main() {
    val i = MyInterceptor()
    println(i.key === ContinuationInterceptor)   // True
}
