package roundtriptests.portablebuilder

import NUnit.Framework.TestAttribute
import roundtrip.portablebuilder.*

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
    }
}
