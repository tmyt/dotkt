import NUnit.Framework.TestAttribute
import OperatorViews.*

class OperatorViewTests {
    @TestAttribute
    fun comparisonUsesTheSameAdapterAndEvaluatesEachOperandOnce() {
        StrictOrder.Trace = ""
        check(StrictOrder.Make(1) < StrictOrder.Make(2))
        check(StrictOrder.Trace == "12<")
        check(StrictOrder.Make(3).compareTo(StrictOrder.Make(2)) == 1)
        check(StrictOrder.Trace == "12<32<>")
        val a = PartialOrder<String>(1)
        val b = PartialOrder<String>(2)
        val calls = a.Calls
        check(a.compareTo(b) == -1 && b.compareTo(a) == 1)
        check(a.compareTo(a) == 0 && PartialOrder<String>(-1).compareTo(a) == 0)
        check(a.Calls == calls + "LGLGLGLG")
        check(Ranked().compareTo(Ranked()) == 17 && Ranked() > Ranked())
    }

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
