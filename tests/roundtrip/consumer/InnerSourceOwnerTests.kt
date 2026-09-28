package roundtrip.innerowner

import NUnit.Framework.TestAttribute

private fun <O, I> read(value: Outer<O>.Inner<I>): I = value.value

class InnerSourceOwnerTests {
    @TestAttribute
    fun inheritedOwnersKeepOwnAndCapturedArgumentsAcrossDlls() {
        val inner = Outer<Int>().Inner<String>("inner")
        check(inner.value == "inner")
        check(read(inner) == "inner")
        val derived = Outer<Int>().Derived<String>("derived")
        check(derived.nested == "derived")
        val wrapped = Outer<Int>().Wrapped<String>(listOf("carrier"))
        check(wrapped.value[0] == "carrier")
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
