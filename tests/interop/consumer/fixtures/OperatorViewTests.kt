import NUnit.Framework.TestAttribute
import OperatorViews.*

class OperatorViewTests {
    @TestAttribute
    fun namedAndUnaryViewsKeepOperandOrderAndGenericOwner() {
        val a = Bits(1)
        val b = Bits(2)
        check(!Bits(0) && a.inv().Value == -2)
        check((a and b).Value == 0 && (a or b).Value == 3 && (a xor b).Value == 3)
        check((a shl 1).Value == 2 && (b shr 1).Value == 1 && (Bits(-2) ushr 1).Value == Int.MAX_VALUE)
        check((a and Operand<String>("right")) == "right")
        check(Operand<Int>(13).inv() == 13)
    }
}
