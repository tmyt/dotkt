import NUnit.Framework.TestAttribute
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
import kotlin.coroutines.EmptyCoroutineContext

private class CovariantContextCompletion<T> : Continuation<T> {
    override val context = EmptyCoroutineContext
    override fun resumeWith(result: Result<T>) {}
}

private interface CovariantContextSlot<T> { val context: CoroutineContext }
private class NestedContinuationContext : CovariantContextSlot<List<Continuation<Int>>> {
    override val context = EmptyCoroutineContext
}
private class NestedResultContext : CovariantContextSlot<List<Result<Int>>> {
    override val context = EmptyCoroutineContext
}
private open class CovariantCompletionBase<T> { open fun completion(): T? = null }
private class NarrowCompletionBase : CovariantCompletionBase<Continuation<Int>>() {
    override fun completion(): Continuation<Int> = CovariantContextCompletion<Int>()
}

class CovariantContinuationContextTests {
    @TestAttribute
    fun constructedBaseAndNestedInterfaceOwnersUseTheSameRepresentation() {
        val continuationSlot: CovariantContextSlot<List<Continuation<Int>>> = NestedContinuationContext()
        val resultSlot: CovariantContextSlot<List<Result<Int>>> = NestedResultContext()
        check(continuationSlot.context === EmptyCoroutineContext)
        check(resultSlot.context === EmptyCoroutineContext)
        val base: CovariantCompletionBase<Continuation<Int>> = NarrowCompletionBase()
        check(base.completion()!!.context === EmptyCoroutineContext)
    }

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
