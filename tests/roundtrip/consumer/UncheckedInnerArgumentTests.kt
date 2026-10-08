package roundtrip.innercast

import NUnit.Framework.TestAttribute
import kotlin.coroutines.*

class UncheckedInnerArgumentTests {
    @TestAttribute
    fun ordinaryCallsAcceptErasedInnerValuesInTheirOwnModule() {
        val reference = InnerCastOwner("reference")
        val value = InnerCastOwner(42)
        val item = reference.Item(7)
        check(reference.ordinary(item) == 7)
        check(value.ordinary(value.Item(11)) == 11)
        // An unchecked owner argument does not change the raw inner classifier or identity.
        check(value.ordinary(item) == 7)
    }

    @TestAttribute
    fun suspendCallsUseTheSameInnerArgumentRepresentation() {
        val reference = InnerCastOwner("reference")
        val value = InnerCastOwner(42)
        val item = reference.Item(7)
        var complete = false
        val action: suspend () -> Unit = {
            check(reference.suspending(item) == 7)
            check(value.suspending(value.Item(11)) == 11)
            check(value.suspending(item) == 7)
            complete = true
        }
        action.startCoroutine(object : Continuation<Unit> {
            override val context: CoroutineContext = EmptyCoroutineContext
            override fun resumeWith(result: Result<Unit>) { result.getOrThrow() }
        })
        check(complete)
    }

    private fun <T> castAndConsume(raw: Any): Int {
        val item = raw as InnerCastOwner<T>.Item
        check(item === raw)
        check(returnInner(item) === raw)
        check(InnerCastHolder(item).item === raw)
        return consumeInner(item)
    }

    @TestAttribute
    fun separatelyCompiledParametersPreserveUncheckedOwnerViewsAndIdentity() {
        val raw: Any = InnerCastOwner("reference").Item(13)
        check(castAndConsume<String>(raw) == 13)
        check(castAndConsume<Int>(raw) == 13)
    }

    private fun <T, U> castAndConsumeGeneric(raw: Any): Int {
        val item = raw as InnerCastOwner<T>.GenericItem<U>
        check(item === raw)
        return consumeGenericInner(item)
    }

    private fun <T, U> castAndCompareGeneric(first: Any, second: Any): Boolean {
        val item = first as InnerCastOwner<T>.GenericItem<U>
        return item.sameCount(second as InnerCastOwner<T>.GenericItem<U>)
    }

    @TestAttribute
    fun innerOwnAndEnclosingFramesRemainIndependent() {
        val raw: Any = InnerCastOwner("reference").GenericItem(42, 17)
        check(castAndConsumeGeneric<String, Int>(raw) == 17)
        check(castAndConsumeGeneric<Int, String>(raw) == 17)
        val other: Any = InnerCastOwner(42).GenericItem("reference", 17)
        check(castAndCompareGeneric<String, Int>(raw, other))
    }
}
