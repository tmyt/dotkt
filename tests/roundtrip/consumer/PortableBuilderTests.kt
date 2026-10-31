package roundtriptests.portablebuilder

import NUnit.Framework.TestAttribute
import roundtrip.portablebuilder.*

private class ImportedBuilderRangeSequence : CharSequence {
    override val length: Int get() = 3
    override fun get(index: Int): Char = "abc"[index]
    override fun subSequence(startIndex: Int, endIndex: Int): CharSequence = "abc".substring(startIndex, endIndex)
    override fun toString(): String = "rendered"
}

class PortableBuilderTests {
    @TestAttribute fun importedBuilderPoliciesPreserveNativeIdentityAndUnicode() {
        val builder = StringBuilder("A😀B")
        val native = builder as System.Text.StringBuilder
        check(reverseBuilder(builder) === builder && native.ToString() == "B😀A")
        check(savedBuilderReverse()(builder) === builder && builder.toString() == "A😀B")
        check(copyBuilder(builder).concatToString() == "A😀B")
        check(deleteBuilder(builder, 1, Int.MAX_VALUE) === builder && builder.toString() == "A")
        check(replaceBuilder(builder, 1, Int.MAX_VALUE, "!") === builder && native.ToString() == "A!")
    }

    @TestAttribute fun importedScalarBodiesAndInlineByteKeepClrRendering() {
        val builder = StringBuilder()
        check(savedBuilderAppend()(builder, true) === builder)
        check(appendBuilderByte(builder, Byte.MIN_VALUE) === builder)
        check(builder.toString() == true.toString() + Byte.MIN_VALUE.toString())
        builder.appendRange(charArrayOf('x', 'y', 'z'), 1, 3)
        builder.insertRange(0, "abcd", 1, 3)
        check(builder.toString() == "bc" + true.toString() + Byte.MIN_VALUE.toString() + "yz")
        val custom: CharSequence = ImportedBuilderRangeSequence()
        check(StringBuilder().appendRange(custom, 1, 3).toString() == "bc")
        check(StringBuilder("!").insertRange(0, custom, 1, 3).toString() == "bc!")
    }
}
