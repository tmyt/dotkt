package roundtriptests.functionvariance

import NUnit.Framework.TestAttribute
import roundtrip.functionvariance.*

private class DerivedConstructorCallbacks : ConstructorCallbacks {
    constructor(callback: (Int) -> Unit): super(callback)
    constructor(callback: (String) -> Unit): super(callback)
}

private class ConsumerGenericCallbacks : GenericCallbackSlots<Int> {
    override fun select(callback: (Int) -> Unit): Int { callback(9); return 9 }
    override fun select(callback: (String) -> Unit): Int { callback("ten"); return 10 }
}

private class DerivedGenericCallbacks : GenericCallbackBase<Int>(11) {
    override fun select(callback: (Int) -> Unit): Int { callback(11); return 11 }
}

class FunctionVarianceTests {
    @TestAttribute
    fun sourceSignaturesAndIdentitySurviveDllBoundaries() {
        val next: (Int) -> Int = increment()
        check(next(4) == 5)
        val length = sequenceLength()
        check(length("across") == 6)
        check(length(StringBuilder("builder")) == 7)
        check(length(null) == null)
        val textFactory: () -> String = stringFactory()
        val sequenceFactory: () -> CharSequence = textFactory
        check(sequenceFactory === textFactory)
        check(sequenceFactory().length == 12)
        check(consumeSequenceFactory(textFactory) == 12)
        val retained: (Int) -> Int = keep(next)
        check(retained === next)
        val holder = FunctionHolder(next)
        val property: (Int) -> Int = holder.callback
        check(property === next)
        check(property(6) == 7)
        check(invokeDefault(9) == 10)
        check(CompanionFunctions.answer == 42)
        val companionFunction: (Int) -> Int = CompanionFunctions.increment
        check(companionFunction(41) == 42)
        check(optional(false) == null)
        val maybe: ((String?) -> String?)? = optional(true)
        check(maybe!!(null) == null)
        check(maybe("text") == "text")
        val outer: (String?) -> ((Int) -> String?) = nested()
        check(outer("nested")(1) == "nested")
        check(outer("nested")(0) == null)
        val receiver: String?.(Int) -> String? = extension()
        check(receiver("receiver", 1) == "receiver")
        check(receiver(null, 1) == null)
        val contextFunction: context(Context) (Int) -> String? = contextual()
        with(Context("context")) { check(contextFunction(1) == "context") }
        check(boxed().value(10) == 11)
        val sink: FunctionSink<(Int) -> Int> = IntFunctionSink()
        check(sink is IntFunctionSink)
        check(FunctionBound(next).apply(41) == 42)
        check(consumeFactoryBox { Box(44) } == 44)
        val readInt: (Box<Int>) -> Int = boxReader()
        val readText: (Box<String>) -> String = boxReader()
        check(readInt(Box(42)) == 42)
        check(readText(Box("generic")) == "generic")
        check(boxedStar(Box("star")) == "star")
        check(inlineIncrement(20) == 21)
        check(inlineIncrement(30, next) == 31)
        val single: () -> Float = { 1.25f }
        val double: () -> Double = { 2.5 }
        check(selected(single) == 1.25f)
        check(selected(double) == 2.5)
        val slots: ReturnSlots = ReturnImplementation()
        check(slots.selected(single) == 1.25f)
        check(slots.selected(double) == 2.5)
        val generic = GenericReturnSlots<Int>()
        val singleArgument: (Int) -> Float = { it.toFloat() }
        val doubleArgument: (Int) -> Double = { it.toDouble() }
        check(generic.selected(3, singleArgument) == 3.0f)
        check(generic.selected(4, doubleArgument) == 4.0)
        var number = 0
        var text = ""
        val numberCallback: (Int) -> Unit = { number = it }
        val textCallback: (String) -> Unit = { text = it }
        check(ConstructorCallbacks(numberCallback).selected == 3)
        check(ConstructorCallbacks(textCallback).selected == 4)
        check(ConstructorCallbacks(numberCallback, true).selected == 3)
        check(ConstructorCallbacks(textCallback, true).selected == 4)
        check(DerivedConstructorCallbacks(numberCallback).selected == 3)
        check(DerivedConstructorCallbacks(textCallback).selected == 4)
        check(number == 3)
        check(text == "four")
        val callbackSlots: CallbackSlots = CallbackImplementation()
        check(callbackSlots.select(numberCallback) == 5)
        check(callbackSlots.select(textCallback) == 6)
        check(number == 5)
        check(text == "six")
        val producerGeneric: GenericCallbackSlots<Int> = GenericCallbackBase(7)
        check(producerGeneric.select(numberCallback) == 7)
        check(producerGeneric.select(textCallback) == 8)
        check(number == 7)
        check(text == "eight")
        val consumerGeneric: GenericCallbackSlots<Int> = ConsumerGenericCallbacks()
        check(consumerGeneric.select(numberCallback) == 9)
        check(consumerGeneric.select(textCallback) == 10)
        check(number == 9)
        check(text == "ten")
        val inherited: GenericCallbackSlots<Int> = DerivedGenericCallbacks()
        check(inherited.select(numberCallback) == 11)
        check(inherited.select(textCallback) == 8)
        check(number == 11)
        check(text == "eight")
    }
}
