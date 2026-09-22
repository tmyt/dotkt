import NUnit.Framework.TestAttribute

private class LocalInlineDefaultReceiver(var value: Int = 1) {
    fun next(result: Int = run { value += 1; value }): Int = result
}

class InlineDefaultReceiverTests {
    @TestAttribute fun localInlineDefaultsKeepReceiverAndFreshBindings() {
        val receiver = LocalInlineDefaultReceiver()
        check(receiver.next() == 2)
        check(receiver.next() == 3)
        check(receiver.next(99) == 99)
        check(receiver.value == 3)
    }
}
