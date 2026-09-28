package roundtrip.innerowner

import NUnit.Framework.TestAttribute

private fun <O, I> read(value: Outer<O>.Inner<I>): I = value.value
private fun <O, I> readNullable(value: NullableOuter<O>.Inner<I>): I? = value.value
private fun <O, M, L> readLeaf(value: NullableOuter<O>.Middle<M>.Leaf<L>): L? = value.value

class InnerSourceOwnerTests {
    @TestAttribute
    fun inheritedOwnersKeepOwnAndCapturedArgumentsAcrossDlls() {
        val inner = Outer<Int>().Inner<String>("inner")
        check(inner.value == "inner")
        check(read(inner) == "inner")
        inner.visit { check(it == "inner") }
        PlainInline(17).visit { check(it == 17) }
        PlainInline("plain").visit { check(it == "plain") }
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
        val nullable = NullableOuter<Int>(7).Inner<String>("nullable")
        check(nullable.value == "nullable")
        check(readNullable(nullable) == "nullable")
        check(nullable.captured == 7)
        check(NullableInnerHolder(nullable).value.value == "nullable")
        val empty = NullableOuter<Int>(null).Inner<String>(null)
        check(empty.value == null)
        check(empty.captured == null)
        val ownValue = NullableOwnOuter<String>().Inner<Int>(31)
        check(ownValue.value == 31)
        val ownEmpty = NullableOwnOuter<String>().Inner<Int>(null)
        check(ownEmpty.value == null)
        val nullableMiddle = NullableOuter<Int>(19).Middle<String>("middle")
        val nullableLeaf = nullableMiddle.Leaf<Double>(2.5)
        check(nullableLeaf.value == 2.5)
        check(readLeaf(nullableLeaf) == 2.5)
        check(nullableLeaf.capturedOuter == 19)
        check(nullableLeaf.capturedMiddle == "middle")
        check(nullableMiddle.Captured(19).value == 19)
        val nullLeaf = NullableOuter<Int>(null).Middle<String>(null).Leaf<Double>(null)
        check(readLeaf(nullLeaf) == null)
        check(nullLeaf.capturedOuter == null)
        check(nullLeaf.capturedMiddle == null)
        val companionLeaf = CompanionOuter<Int>(11).Middle<String>("middle").Leaf<Double>(4.5)
        check(readCompanionLeaf(companionLeaf) == 4.5)
        val emptyCompanionLeaf = CompanionOuter<Int>(null).Middle<String>(null).Leaf<Double>(null)
        check(readCompanionLeaf(emptyCompanionLeaf) == null)
        NullableOwnOuter<String>().Inline<Int>(listOf(31)).visit { check(it == 31) }
        NullableOwnOuter<String>().Inline<Int>(listOf(null)).visit { check(it == null) }
    }
}
