package roundtriptests.inneraccessors

import NUnit.Framework.TestAttribute
import roundtrip.inneraccessors.*

class ConsumerOuter<X> : Outer<X>() {
    inner class Child<T> : Body<T>()
}

class ConsumerDeep<X> : DeepOuter<X>() {
    inner class ConsumerMiddle<Y> : Middle<Y>() {
        inner class Child<T> : Body<T>()
    }
}

private fun <X, T> checkGenericAccessor() {
    val outer = ConsumerOuter<X>()
    val child = outer.Child<T>()
    val sink: Sink<T> = child
    check(outer.storage.value == null)
    check(child.context == 17 && sink.context == 17)
}

private fun <X, Y, T> checkDeepAccessor() {
    val outer = ConsumerDeep<X>()
    val middle = outer.ConsumerMiddle<Y>()
    val child = middle.Child<T>()
    val sink: Sink<T> = child
    check(outer.storage.value == null && middle.middleStorage.value == null)
    check(child.context == 29 && sink.context == 29)
}

class InnerAccessorFrameTests {
    @TestAttribute
    fun inheritedAccessorClosesOuterNullableCompanion() {
        val child = ConsumerOuter<Int>().Child<String>()
        val sink: Sink<String> = child
        check(child.context == 17 && sink.context == 17)
    }

    @TestAttribute
    fun ownAndOuterArgumentsKeepIndependentRepresentations() {
        checkGenericAccessor<Int, String>()
        checkGenericAccessor<String, Int>()
        checkGenericAccessor<Int?, String?>()
        checkGenericAccessor<String?, Int?>()
    }

    @TestAttribute
    fun nestedApplicationsCloseThroughCallerGenericFrame() {
        checkGenericAccessor<Cell<Int>, Cell<String?>>()
        checkGenericAccessor<Cell<String?>, Cell<Int>>()
    }

    @TestAttribute
    fun multipleEnclosingCompanionsRetainDeclarationOrder() {
        checkDeepAccessor<Int, String, Boolean>()
        checkDeepAccessor<String, Boolean, Int>()
        checkDeepAccessor<Int?, Boolean?, String?>()
    }
}
