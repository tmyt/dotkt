package roundtrip.innerowner

import NUnit.Framework.TestAttribute

class InnerSourceOwnerTests {
    @TestAttribute
    fun inheritedOwnersKeepOwnAndCapturedArgumentsAcrossDlls() {
        val inner = Outer<Int>().Inner<String>("inner")
        check(inner.value == "inner")
        val captured = Outer<Int>().Captured(17)
        check(captured.value == 17)
        val middle = Outer<Int>().Middle<String>()
        val leaf = middle.Leaf<Double>(3.5)
        val outerValue = middle.OuterValue(23)
        val middleValue = middle.MiddleValue("middle")
        check(leaf.value == 3.5)
        check(outerValue.value == 23)
        check(middleValue.value == "middle")
    }
}
