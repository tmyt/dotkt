package roundtriptests.continuationinheritance

import NUnit.Framework.TestAttribute
import kotlin.coroutines.Continuation
import kotlin.coroutines.EmptyCoroutineContext
import roundtrip.continuationinheritance.*

class ConsumerChild<T> : CompletionBody<T>()
class StringChild : ProducerChild<String>()
class IntChild : ProducerChild<Int>()
class NullableChild<T> : NullableCompletionBody<T>()
class FrameChild<T> : FrameCompletionBody<T>()
class BoundChild<C : Continuation<String>>(target: C) : CompletionBound<C>(target)

class ContinuationInheritanceTests {
    @TestAttribute
    fun genericInheritedImplementationKeepsSuccessAndFailureDispatch() {
        val strings = ConsumerChild<String>()
        val stringSlot: Continuation<String> = strings
        check(stringSlot.context === EmptyCoroutineContext)
        stringSlot.resumeWith(Result.success("string"))
        check(strings.completion!!.getOrThrow() == "string")
        val error = IllegalStateException("failure")
        stringSlot.resumeWith(Result.failure(error))
        check(strings.completion!!.exceptionOrNull() === error)
        check(strings.resumeCount == 2)

        val ints = ConsumerChild<Int>()
        val intSlot: Continuation<Int> = ints
        intSlot.resumeWith(Result.success(42))
        check(ints.completion!!.getOrThrow() == 42)
        check(ints.resumeCount == 1)
    }

    @TestAttribute
    fun specializedAndNullableSubclassesNeedNoRepeatedOverride() {
        val strings = StringChild()
        val stringSlot: Continuation<String> = strings
        stringSlot.resumeWith(Result.success("specialized"))
        check(strings.completion!!.getOrThrow() == "specialized")
        val ints = IntChild()
        val intSlot: Continuation<Int> = ints
        intSlot.resumeWith(Result.success(17))
        check(ints.completion!!.getOrThrow() == 17)

        val nullable = NullableChild<Int>()
        val nullableSlot: Continuation<Int?> = nullable
        nullableSlot.resumeWith(Result.success(null))
        check(nullable.completion!!.isSuccess)
        check(nullable.completion!!.getOrThrow() == null)
        nullableSlot.resumeWith(Result.success(23))
        check(nullable.completion!!.getOrThrow() == 23)
    }

    @TestAttribute
    fun sourceInheritanceAndBoundsSurvivePhysicalCompanionFrames() {
        val frame = FrameChild<Int>()
        val slot: Continuation<Int> = frame
        slot.resumeWith(Result.success(31))
        check(frame.value == 31)
        val strings = ConsumerChild<String>()
        val bound = BoundChild(strings)
        val target: Continuation<String> = bound.target
        target.resumeWith(Result.success("bound"))
        check(strings.completion!!.getOrThrow() == "bound")
    }
}
