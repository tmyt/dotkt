package continuationoverride

import kotlin.coroutines.AbstractCoroutineContextElement
import kotlin.coroutines.Continuation
import kotlin.coroutines.ContinuationInterceptor

abstract class RoundtripContinuationDispatcher :
    AbstractCoroutineContextElement(ContinuationInterceptor),
    ContinuationInterceptor {
    final override fun <T> interceptContinuation(continuation: Continuation<T>): Continuation<T> =
        continuation
}
