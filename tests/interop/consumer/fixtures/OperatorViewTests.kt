import NUnit.Framework.TestAttribute
import OperatorViews.*
import ExplicitOperators.*
import kotlin.clr.byref

private infix fun Bits.and(right: Operand<Double>): Double = right.Value + 1.0

class OperatorViewTests {
    @TestAttribute
    fun equalitySelectsTheTwoNativeOperatorsIndependently() {
        val left = Left()
        val right = Right<String>()
        check(left == right && left != right)
        check(42 == right && 41 != right)
        val negated = !(left == right)
        check(!negated)
        val k = Key(3)
        val other = Key(3)
        check(k == other && k != other && !k.equals(other) && k !== other)
        val erased: Any = k
        val erasedOther: Any = other
        check(erased != erasedOther)
        check(other !in setOf(k))
        fun <T> same(a: T, b: T): Boolean = a == b
        check(!same(k, other))
        fun <T : Key> bounded(a: T, b: T): Boolean = a == b
        check(bounded(k, other))
        fun <T> maybe(a: Key, b: T): Boolean = a == b
        check(!maybe(k, other) && !maybe(k, null))
        check(!(k.Oblivious() == other))
        fun nullable(a: Key?, b: Key): Boolean {
            val before = a == b
            if (a == null) return !before
            return !before && a == b && a != b
        }
        check(nullable(k, other) && nullable(null, other))
        var calls = 0
        fun next(): Key { calls++; return k }
        check(next() != next() && calls == 2)
    }

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
        check(Left() < Key(1))
        val compare: (StrictOrder, StrictOrder) -> Int = StrictOrder::compareTo
        check(compare(StrictOrder(4), StrictOrder(1)) > 0)
        ReadonlyOrder.Trace = ""
        check(ReadonlyOrder.Make(3).compareTo(ReadonlyOrder.Make(2)) == 1)
        check(ReadonlyOrder.Trace == "32<>")
        check(2 < ReadonlyOrder.Make(3) && ReadonlyOrder.Trace == "32<>3LG")
        var first = ReadonlyOrder(1)
        var second = ReadonlyOrder(2)
        check(first < 2)
        val left = BorrowLeft<Int>()
        check(first < Ticket(2) && left < BorrowRight<Int>() && left < left)
        check(ReadonlyOrder.op_LessThan(byref(first), byref(second)))
        var counter = 0L
        check(ReadonlyOrder.op_LessThan(byref(first), byref(counter)) && counter == 1L)
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
        check((a and Operand<Double>(2.0)) == 3.0)
        val inverse: () -> Bits = a::inv
        check(inverse().Value == -2)
    }
}
