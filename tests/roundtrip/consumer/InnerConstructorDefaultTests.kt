package roundtriptests.innerdefaults

import NUnit.Framework.TestAttribute
import roundtrip.innerdefaults.*
import kotlin.coroutines.*

class InnerConstructorDefaultTests {
    @TestAttribute
    fun enclosingDefaultsAcrossDllsPreserveEvaluationAndInitialization() {
        val outer = InnerDefaultOuter(19)
        var evaluations = 0
        fun middle(): InnerDefaultOuter.Middle { evaluations++; return outer.Middle(4) }
        val leaf = middle().Leaf()
        check(evaluations == 1)
        check(outer.reads == 1)
        check(leaf.value == 23 && leaf.next == 24 && leaf.initialized == 23)
        check(leaf.memberDefault() == 23)
        val explicit = middle().Leaf(31)
        check(evaluations == 2 && outer.reads == 1)
        check(explicit.value == 31 && explicit.next == 32 && explicit.initialized == 23)
        val callback = middle().Callback()
        check(evaluations == 3 && outer.reads == 1)
        check(callback.read() == 23 && outer.reads == 2)
        check(middle().Deeper(2).Leaf().value == 25)
        check(evaluations == 4 && outer.reads == 3)
    }

    @TestAttribute
    fun genericEnclosingDefaultsRetainReferenceValueAndNullableFrames() {
        val text = GenericInnerDefaultOuter("seed").Middle(7).Leaf()
        check(text.value == "seed" && text.own == 7)
        check(text.afterConstruction() == "seed" && text.memberDefault() == "seed")
        val number = GenericInnerDefaultOuter(19).Middle("middle").Leaf()
        check(number.value == 19 && number.own == "middle")
        check(number.afterConstruction() == 19 && number.memberDefault() == 19)
        val nullable = GenericInnerDefaultOuter<Int?>(null).Middle<String?>(null).Leaf()
        check(nullable.value == null && nullable.own == null)
        check(nullable.afterConstruction() == null && nullable.memberDefault() == null)
        check(GenericInnerDefaultOuter("callback").Middle(7).Callback().read() == "callback")
        check(GenericInnerDefaultOuter(23).Middle("callback").Callback().read() == 23)
        check(GenericInnerDefaultOuter<Int?>(null).Middle("nullable").Callback().read() == null)
        @Suppress("UNCHECKED_CAST")
        val widened = GenericInnerDefaultOuter(29) as GenericInnerDefaultOuter<Any?>
        val widenedMiddle = widened.Middle("widened")
        check(widenedMiddle.Leaf().value == 29)
        check(widenedMiddle.Callback().read() == 29)
        check(widenedMiddle.Deeper(true).Leaf().value == 29)
        check(widenedMiddle.Deeper(true).Callback().read() == 29)
        check(GenericInnerDefaultOuter("deep").Middle(7).Deeper(false).Callback().read() == "deep")
        check(widenedMiddle.Sibling().sibling.middle == 1)
        check(widenedMiddle.Deeper(true).SamCallback().reader.read() == 29)
        var completed = false
        widenedMiddle.Deeper(true).SuspendCallback().read.startCoroutine(object : Continuation<Any?> {
            override val context = EmptyCoroutineContext
            override fun resumeWith(result: Result<Any?>) {
                check(result.getOrThrow() == 29)
                completed = true
            }
        })
        check(completed)
    }
}
