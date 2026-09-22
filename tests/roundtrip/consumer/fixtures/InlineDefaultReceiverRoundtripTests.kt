import NUnit.Framework.TestAttribute
import inlinedefaults.receiverLength

private class InlineDefaultDerived : inlinedefaults.Base()

class InlineDefaultReceiverRoundtripTests {
    @TestAttribute fun importedInlineDefaultsKeepReceiverAndFreshBindings() {
        val receiver = inlinedefaults.Receiver()
        check(receiver.next() == 2)
        check(receiver.next() == 3)
        check(receiver.next(99) == 99)
        check(receiver.field == 3)
        check(receiver.nested() == 4)
        check(receiver.nested() == 5)
    }

    @TestAttribute fun importedInlineDefaultsPreserveEvaluationOrder() {
        val receiver = inlinedefaults.Receiver()
        fun getReceiver(): inlinedefaults.Receiver { receiver.mark("R"); return receiver }
        fun supplied(): Int { receiver.mark("S"); return 3 }
        check(getReceiver().ordered(supplied = supplied()) == 26)
        check(receiver.trace == "RSDE")
    }

    @TestAttribute fun importedInlineDefaultsKeepGenericAndExtensionReceivers() {
        val text = inlinedefaults.GenericReceiver("old")
        check(text.assign("new") == "new")
        check(text.field == "new")
        val number = inlinedefaults.GenericReceiver(1)
        check(number.assign(7) == 7)
        check(number.field == 7)
        check("receiver".receiverLength() == 8)
    }

    @TestAttribute fun carriedHelpersAndConstructorDefaultsExpandInlineCalls() {
        check(inlinedefaults.callback() == 41)
        check(inlinedefaults.Base().value == 42)
        check(InlineDefaultDerived().value == 42)
    }
}
