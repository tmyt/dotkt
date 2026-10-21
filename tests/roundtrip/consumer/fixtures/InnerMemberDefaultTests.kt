import NUnit.Framework.TestAttribute
import roundtrip.innermemberdefaults.MemberDefaultOuter
import roundtrip.innermemberdefaults.GenericMemberDefaultOuter

private var innerMemberReceiverCalls = 0
private fun innerMemberReceiver(value: MemberDefaultOuter.Inner): MemberDefaultOuter.Inner {
    innerMemberReceiverCalls++
    return value
}

class InnerMemberDefaultTests {
    @TestAttribute fun omittedDefaultsUseTheCallsOwnReceiverExactlyOnce() {
        val first = MemberDefaultOuter(11)
        val second = MemberDefaultOuter(22)
        check(first.Inner().value() == 11 && second.Inner().value() == 22)
        innerMemberReceiverCalls = 0
        check(innerMemberReceiver(first.Inner()).counted() == 11)
        check(innerMemberReceiver(second.Inner()).counted() == 22)
        check(innerMemberReceiverCalls == 2 && first.reads == 1 && second.reads == 1)
        check(innerMemberReceiver(first.Inner()).counted(33) == 33)
        check(innerMemberReceiverCalls == 3 && first.reads == 1)
    }

    @TestAttribute fun nestedGenericDefaultsCloseTheirActualEnclosingFrames() {
        check(GenericMemberDefaultOuter("outer").Middle(41).pair() == Pair("outer", 41))
        check(GenericMemberDefaultOuter(16).Middle("own").pair() == Pair(16, "own"))
        check(GenericMemberDefaultOuter<Int?>(null).Middle("own").pair() == Pair(null, "own"))
        check(GenericMemberDefaultOuter("outer").Middle<Int?>(null).pair() == Pair("outer", null))
        val text = GenericMemberDefaultOuter("text").Middle(42).Leaf()
        check(text.outerValue() == "text" && text.middleValue() == 42)
        val number = GenericMemberDefaultOuter(17).Middle("middle").Leaf()
        check(number.outerValue() == 17 && number.middleValue() == "middle")
        val nullable = GenericMemberDefaultOuter<Int?>(null).Middle<String?>(null).Leaf()
        check(nullable.outerValue() == null && nullable.middleValue() == null)
    }

    @TestAttribute fun carriedClosuresRetainTheLexicalEnclosingCapture() {
        val outer = MemberDefaultOuter(23)
        check(outer.Inner().callback() == 23 && outer.reads == 1)
        check(outer.Inner().hiddenDefault() == 123 && outer.Inner().hiddenCallback() == 123)
        check(GenericMemberDefaultOuter("captured").Middle(1).Leaf().callback() == "captured")
        check(GenericMemberDefaultOuter(42).Middle("middle").Leaf().callback() == 42)
        check(GenericMemberDefaultOuter<Int?>(null).Middle(1).Leaf().callback() == null)
    }

    @TestAttribute fun inlinePayloadAndOrdinaryCallsRetainEnclosingAccess() {
        check(MemberDefaultOuter(24).Inner().withSeed { it + 1 } == 25)
        check(GenericMemberDefaultOuter("inline").Middle(1).Leaf().withSeed { it + "!" } == "inline!")
        check(GenericMemberDefaultOuter(43).Middle("middle").Leaf().withSeed { it + 1 } == 44)
        check(GenericMemberDefaultOuter<Int?>(null).Middle(1).Leaf().withSeed { it } == null)
        check(MemberDefaultOuter(24).Inner().inlineValue() == 24)
        check(GenericMemberDefaultOuter("inline").Middle(1).Leaf().inlineValue() == "inline")
        check(GenericMemberDefaultOuter(43).Middle("middle").Leaf().inlineValue() == 43)
    }
}
