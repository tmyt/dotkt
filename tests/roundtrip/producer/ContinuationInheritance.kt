package roundtrip.continuationinheritance

import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

open class CompletionBody<T> : Continuation<T> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    var completion: Result<T>? = null
    var resumeCount: Int = 0
    override fun resumeWith(result: Result<T>) {
        completion = result
        resumeCount++
    }
}

open class ProducerChild<T> : CompletionBody<T>()

open class NullableCompletionBody<T> : Continuation<T?> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    var completion: Result<T?>? = null
    override fun resumeWith(result: Result<T?>) { completion = result }
}

class CompletionCell<T>(val value: T)

// A constructed nullable argument allocates a companion slot; the inheritance carrier keeps source T's index.
open class FrameCompletionBody<T> : Continuation<T> {
    override val context: CoroutineContext get() = EmptyCoroutineContext
    val storage = CompletionCell<T?>(null)
    var value: T? = null
    override fun resumeWith(result: Result<T>) { value = result.getOrNull() }
}

open class CompletionBound<C : Continuation<String>>(val target: C)

open class CompletionOuter<X> {
    val storage = CompletionCell<X?>(null)
    open inner class Body<T> : Continuation<T> {
        override val context: CoroutineContext get() = EmptyCoroutineContext
        var completion: Result<T>? = null
        override fun resumeWith(result: Result<T>) { completion = result }
    }
}
