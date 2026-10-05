import NominalFunctionSlots.CallbackHost
import NominalFunctionSlots.Callbacks

private class NominalFunctionHost : CallbackHost() {
    override fun Apply(callback: System.Func2<Int, Int>, value: Int): Int = callback(value)
}

class NominalFunctionDelegateTests {
    @NUnit.Framework.TestAttribute
    fun nativeSlotsKeepExactSignaturesAndAdaptFunctionValues() {
        check(Callbacks.Apply({ it + 1 }, 5) == 6)
        val stored: (Int) -> Int = { it + 3 }
        check(Callbacks.Apply(stored, 7) == 10)
        val native = System.Func2<Int, Int>(stored)
        check(native(10) == 13)
        check(Callbacks.Identity(native) === native)
        check(Callbacks.Dispatch(NominalFunctionHost()) == 42)
        var calls = 0
        Callbacks.Run { calls++ }
        check(calls == 1)
        var total = 0
        val result = Callbacks.UseSpan({ span -> total = Callbacks.SpanTotal(span); Unit }, intArrayOf(1, 2, 3))
        check(result === Unit)
        check(total == 6)
        val storedSpan: (kotlin.clr.Span<Int>) -> Unit = { span -> total = Callbacks.SpanTotal(span) }
        val storedResult = Callbacks.UseSpan(storedSpan, intArrayOf(2, 3, 4))
        check(storedResult === Unit)
        check(total == 9)
        val spanIdentity: (kotlin.clr.Span<Int>) -> kotlin.clr.Span<Int> = { it }
        check(Callbacks.TransformSpan(spanIdentity, intArrayOf(3, 4, 5)) == 12)
        val backing = intArrayOf(3, 4, 5)
        val spanFactory: () -> kotlin.clr.Span<Int> = { Callbacks.ArraySpan(backing) }
        check(Callbacks.ProducedSpanTotal(spanFactory) == 12)
    }
}
