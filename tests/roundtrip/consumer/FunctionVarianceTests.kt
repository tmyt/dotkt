package roundtriptests.functionvariance

import NUnit.Framework.TestAttribute
import kotlin.clr.ClrRef
import kotlin.clr.byref
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

private class ConsumerInheritedCallbacks : CallbackParent(), InheritedCallbackSlot

private fun replaceCallback(slot: ClrRef<(Int) -> Int>, next: (Int) -> Int) { slot.value = next }

private class ConsumerGenericInheritedCallbacks : GenericInheritedCallbackParent<Int>(43), GenericInheritedCallbackSlot<Int>

class FunctionVarianceTests {
    private class Direct : (Int) -> Int {
        override fun invoke(p1: Int) = p1 * 2
        override fun toString() = "direct"
    }
    private class FromDefault : DefaultUnary
    private class CallbackChild(value: NominalUnary) : NominalCallbackBase(value)
    private class Echo<T> : (T) -> T { override fun invoke(p1: T): T = p1 }
    private class TwoArities : NominalTwice(), () -> Int { override fun invoke() = 7 }
    private class Binary : NominalBinary {
        var seen = ""
        override fun invoke(p1: String, p2: Int): Any? { seen = p1 + p2; return Unit }
    }

    @TestAttribute
    fun nominalFunctionsCanBePassedAndRetainTheirIdentity() {
        val direct = Direct()
        val function: (Int) -> Int = direct
        check(retainFunctionValue(function)(21) == 42)
        check((retainFunctionValue(function) as Any) === direct)
        val fromBox = boxedValue(function).value
        val fromList = singletonValue(function).single()
        val fromSet = singletonSetValue(function).single()
        check(fromBox(21) == 42 && (fromBox as Any) === direct)
        check(fromList(21) == 42 && (fromList as Any) === direct)
        check(fromSet(21) == 42 && (fromSet as Any) === direct)
        val native = NativeListStorage(ArrayList<(Int) -> Int>())
        native.put(boxedValue(function))
        check(native.values[0](21) == 42 && (native.values[0] as Any) === direct)
        val lambda: (Int) -> Int = { it + 1 }
        check(retainFunctionValue(lambda) === lambda)
        check(retainFunctionValue<((Int) -> Int)?>(null) == null)
        val writable = WritableCallback<String>(lambda)
        replaceCallback(byref(writable.callback), function)
        check(writable.callback(21) == 42)
        check(invokeDefault(21, direct) == 42)
        val imported: NominalUnary = NominalTwice()
        check(imported(21) == 42 && CallbackChild(imported).callback(21) == 42)
        check(FunctionBound(NominalTwice()).apply(21) == 42)
        val echo: (String) -> String = Echo<String>()
        check(echo("generic") == "generic")
        var callback: (Int) -> Int = imported
        check(callback(21) == 42 && callback === unaryView(imported))
        check((callback as Any) === (imported as Any))
        callback = direct
        check((callback as Any) === direct && (direct::invoke as Any) !== direct)
        check(callback.toString() == "direct")
        val multi = TwoArities()
        val zero: () -> Int = multi
        val one: (Int) -> Int = multi
        check(zero() == 7 && one(21) == 42 && (zero as Any) === (one as Any))
        check(invokeDefault(21, FromDefault()) == 22)
        val binary = Binary()
        val nominal: NominalBinary = binary
        @Suppress("UNCHECKED_CAST")
        val erased = nominal as Function2<String, Int, Unit>
        erased("ok", 2)
        check(binary.seen == "ok2")
    }

    @TestAttribute
    fun sourceSignaturesAndIdentitySurviveDllBoundaries() {
        val next: (Int) -> Int = increment()
        check(next(4) == 5)
        check(callbackInnerFactory() == 1)
        var inheritedObserved = 0
        val inheritedSlot: InheritedCallbackSlot = InheritedCallbackChild()
        check(inheritedSlot.handle { value: Int -> inheritedObserved = value } == 31)
        check(inheritedObserved == 31)
        var inheritedText = ""
        check(inheritedSlot.handle { value: String -> inheritedText = value } == 32)
        check(inheritedText == "thirty-two")
        val importedInheritedSlot: InheritedCallbackSlot = ConsumerInheritedCallbacks()
        inheritedObserved = 0
        inheritedText = ""
        check(importedInheritedSlot.handle { value: Int -> inheritedObserved = value } == 31)
        check(importedInheritedSlot.handle { value: String -> inheritedText = value } == 32)
        check(inheritedObserved == 31 && inheritedText == "thirty-two")
        val genericInherited: GenericInheritedCallbackSlot<Int> = GenericInheritedCallbackChild()
        check(genericInherited.handle { value: Int -> inheritedObserved = value } == 41)
        check(genericInherited.handle { value: String -> inheritedText = value } == 42)
        check(inheritedObserved == 40 && inheritedText == "generic-inherited")
        val importedGenericInherited: GenericInheritedCallbackSlot<Int> = ConsumerGenericInheritedCallbacks()
        check(importedGenericInherited.handle { value: Int -> inheritedObserved = value } == 41)
        check(importedGenericInherited.handle { value: String -> inheritedText = value } == 42)
        check(inheritedObserved == 43 && inheritedText == "generic-inherited")
        val delegated: CallbackSlots = DelegatedCallbacks(CallbackImplementation())
        var delegatedNumber = 0
        var delegatedText = ""
        check(delegated.select { value: Int -> delegatedNumber = value } == 5)
        check(delegated.select { value: String -> delegatedText = value } == 6)
        check(delegatedNumber == 5 && delegatedText == "six")
        val length = sequenceLength()
        check(length("across") == 6)
        check(length(StringBuilder("builder")) == 7)
        check(length(null) == null)
        val builder = StringBuilder()
        builder.append("helper")
        builder.append(null as String?)
        check(builder.toString() == "helpernull")
        val replacement: (MatchResult) -> CharSequence = { it.value.uppercase() }
        check(Regex("[a-z]+").replace("one 2 two", replacement) == "ONE 2 TWO")
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
