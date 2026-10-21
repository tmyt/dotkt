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

class ConstructedOuter<X> : Outer<Cell<X>>() {
    inner class Child<T> : Body<Cell<T?>>()
}

class NullableOuter<X> : Outer<X?>() {
    inner class Child<T> : Body<T?>()
}

class TypedConsumer<X>(seed: X) : TypedOuter<X>(seed) {
    inner class Child<T>(item: T) : Body<T>(item)
}

class PlainConsumer<X>(seed: X) : PlainOuter<X>(seed) {
    inner class Child<T>(item: T) : Body<T>(item)
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

private fun <X, T> checkTypedAccessor(seed: X, item: T) {
    val child = TypedConsumer(seed).Child(item)
    val sink: Values<T, X> = child
    check(child.own == item && child.enclosing == seed)
    check(sink.own == item && sink.enclosing == seed)
    val plain = PlainConsumer(seed).Child(item)
    val plainSink: Values<T, X> = plain
    check(plain.own == item && plain.enclosing == seed)
    check(plainSink.own == item && plainSink.enclosing == seed)
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
    fun genericCallersRetainConstructedArguments() {
        checkGenericAccessor<Cell<Int>, Cell<String?>>()
        checkGenericAccessor<Cell<String?>, Cell<Int>>()
    }

    @TestAttribute
    fun constructedAndNullableSupertypeArgumentsCloseAtTheDeclaration() {
        val constructed = ConstructedOuter<Int>().Child<String>()
        val constructedSink: Sink<Cell<String?>> = constructed
        check(constructed.context == 17 && constructedSink.context == 17)
        val nullable = NullableOuter<Int>().Child<String>()
        val nullableSink: Sink<String?> = nullable
        check(nullable.context == 17 && nullableSink.context == 17)
    }

    @TestAttribute
    fun typedGetterSlotsKeepOwnAndEnclosingArgumentsDistinct() {
        checkTypedAccessor(41, "item")
        checkTypedAccessor("seed", 43)
        checkTypedAccessor<Int?, String?>(null, "present")
        checkTypedAccessor<String?, Int?>("present", null)
        checkTypedAccessor<Int?, String?>(null, null)
    }

    @TestAttribute
    fun multipleEnclosingCompanionsRetainDeclarationOrder() {
        checkDeepAccessor<Int, String, Boolean>()
        checkDeepAccessor<String, Boolean, Int>()
        checkDeepAccessor<Int?, Boolean?, String?>()
    }
}
