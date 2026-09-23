import NUnit.Framework.TestAttribute
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

private class CovariantContextCompletion<T> : Continuation<T> {
    override val context = EmptyCoroutineContext
    override fun resumeWith(result: Result<T>) {}
}

class CovariantContinuationContextTests {
    @TestAttribute
    fun anonymousContextDispatchesThroughTheErasedInterface() {
        val integers = object : Continuation<Int> {
            override val context = EmptyCoroutineContext
            override fun resumeWith(result: Result<Int>) {}
        }
        val strings = object : Continuation<String> {
            override val context = EmptyCoroutineContext
            override fun resumeWith(result: Result<String>) {}
        }
        check(integers.context === EmptyCoroutineContext)
        check(strings.context === EmptyCoroutineContext)
        val integerInterface: Continuation<Int> = integers
        val stringInterface: Continuation<String> = strings
        check(integerInterface.context === EmptyCoroutineContext)
        check(stringInterface.context === EmptyCoroutineContext)
        integerInterface.resumeWith(Result.success(7))
        stringInterface.resumeWith(Result.success("ok"))
    }

    @TestAttribute
    fun namedGenericContextDispatchesThroughTheErasedInterface() {
        val integers = CovariantContextCompletion<Int>()
        val strings = CovariantContextCompletion<String>()
        check(integers.context === EmptyCoroutineContext)
        check(strings.context === EmptyCoroutineContext)
        val integerInterface: Continuation<Int> = integers
        val stringInterface: Continuation<String> = strings
        val context: CoroutineContext = integerInterface.context
        check(context === EmptyCoroutineContext)
        check(stringInterface.context === EmptyCoroutineContext)
        integerInterface.resumeWith(Result.success(9))
        stringInterface.resumeWith(Result.success("named"))
    }
}
