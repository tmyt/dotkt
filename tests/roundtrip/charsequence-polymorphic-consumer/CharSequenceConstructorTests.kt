package roundtriptests.charsequencepolymorphic

import NUnit.Framework.TestAttribute

private class ConstructorValue<T>(val source: T)

private open class ConstructorText(val source: CharSequence?) {
    constructor(marker: String, source: String) : this(marker + source)
    fun length() = source?.length ?: -1
}
private class DerivedText(a: String, b: String) : ConstructorText(a + b)
private class OuterText<T>(val outer: T) {
    open inner class Base<U>(val value: U)
    inner class Child(text: String) : Base<CharSequence>(text)
    fun textLength(text: String) = Child(text).value.length
}

class CharSequenceConstructorTests {
    @TestAttribute
    fun selectedConstructorAdaptsStringsAndPreservesNullAndExistingViews() {
        var reads = 0
        fun read(value: String?): String? { reads++; return value }
        check(ConstructorText(read("abc")).length() == 3)
        check(ConstructorText(read(null)).length() == -1)
        check(reads == 2)
        check(ConstructorText("a", "bc").length() == 3)
        check(DerivedText("a", "bc").length() == 3)
        check(OuterText(42).textLength("abc") == 3)
        check(ConstructorValue<CharSequence>("ab").source.length == 2)
        val custom = object : CharSequence {
            override val length: Int get() = 17
            override fun get(index: Int): Char = 'x'
            override fun subSequence(startIndex: Int, endIndex: Int): CharSequence = this
        }
        check(ConstructorText(custom).source === custom)
    }
}
