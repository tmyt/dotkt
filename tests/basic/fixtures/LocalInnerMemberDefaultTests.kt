import NUnit.Framework.TestAttribute

private class LocalMemberDefaultOuter<T>(val seed: T) {
    inner class Middle<U>(val middle: U) {
        inner class Leaf {
            fun outerValue(value: T = seed): T = value
            fun middleValue(value: U = middle): U = value
        }
    }
}

class LocalInnerMemberDefaultTests {
    @TestAttribute fun localDefaultsUseTheirOwnEnclosingInstances() {
        val first = LocalMemberDefaultOuter("first").Middle(42).Leaf()
        val second = LocalMemberDefaultOuter(17).Middle("second").Leaf()
        check(first.outerValue() == "first" && first.middleValue() == 42)
        check(second.outerValue() == 17 && second.middleValue() == "second")
    }
}
