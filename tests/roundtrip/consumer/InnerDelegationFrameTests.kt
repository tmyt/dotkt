package roundtriptests.innerdelegation

import NUnit.Framework.TestAttribute
import roundtrip.innerdelegation.*

class ClosedDelegation : DelegationOuter<String>("closed") {
    inner class Child : Base<Int>(supplied()) {
        init { trace += "B" }
    }
    inner class Explicit : Base<Int>(23, "explicit")
}

class GenericDelegation<A, B>(seed: B, extra: A) : DelegationBridge<A, B>(seed, extra) {
    inner class Child : Base<A>(extra)
    inner class MiddleChild(middle: A) : Middle<A>(middle) {
        inner class LeafChild : Leaf<Int>(29, readSeed())
        inner class CapturedChild : CapturedLeaf<Int>(59)
    }
}

class InnerDelegationFrameTests {
    @TestAttribute
    fun closedImportedDelegationUsesDeclaredOuterAndEvaluatesDefaultOnce() {
        val outer = ClosedDelegation()
        val child = outer.Child()
        check(child.value == "closed" && child.own == 7)
        check(outer.reads == 1 && outer.trace == "ADB")
        val explicit = outer.Explicit()
        check(explicit.value == "explicit" && explicit.own == 23)
        check(outer.reads == 1 && outer.trace == "ADB")
    }

    @TestAttribute
    fun reorderedGenericCallerFramesSurviveInheritedEnclosingTypes() {
        val outer = GenericDelegation("reference", 13)
        val child = outer.Child()
        check(child.value == "reference" && child.own == 13 && outer.reads == 1)
        val number = GenericDelegation(31, "own")
        check(number.Child().value == 31 && number.Child().own == "own" && number.reads == 2)
        val nullable = GenericDelegation<String?, Int?>(null, null)
        val nullChild = nullable.Child()
        check(nullChild.value == null && nullChild.own == null && nullable.reads == 1)
    }

    @TestAttribute
    fun multipleEnclosingFramesCloseOwnAndCapturedParameters() {
        val outer = GenericDelegation("outer", 11)
        val middle = outer.MiddleChild(19)
        val leaf = middle.LeafChild()
        check(leaf.value == "outer" && leaf.fromMiddle == 19 && leaf.own == 29)
        check(outer.reads == 1)
        val captured = middle.CapturedChild()
        check(captured.value == "outer" && captured.fromMiddle == 19 && captured.own == 59)
        check(outer.reads == 2)
        val nullable = GenericDelegation<Int?, String?>(null, null)
        val nullLeaf = nullable.MiddleChild(null).LeafChild()
        check(nullLeaf.value == null && nullLeaf.fromMiddle == null && nullLeaf.own == 29)
        val nullCaptured = nullable.MiddleChild(null).CapturedChild()
        check(nullCaptured.value == null && nullCaptured.fromMiddle == null && nullCaptured.own == 59)
        check(nullable.reads == 2)
    }

    @TestAttribute
    fun sameModuleAndThisDelegationRetainDefaultsAndOrdering() {
        val text = LocalDelegation("same-module")
        check(text.Child().value == "same-module" && text.trace == "ADB" && text.reads == 1)
        check(text.readGrandparentDefault() == "same-module" && text.reads == 2)
        val number = LocalDelegation(37)
        check(number.Child().value == 37 && number.reads == 1)
        check(number.readGrandparentDefault() == 37 && number.reads == 2)
        val nullable = LocalDelegation<Int?>(null)
        check(nullable.Child().value == null && nullable.reads == 1)
        check(nullable.readGrandparentDefault() == null && nullable.reads == 2)
        checkSecondaryDelegation()
        checkOmittedThisDefaults()
        checkLiftedDelegationFrames()
    }
}
