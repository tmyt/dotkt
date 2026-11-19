import NUnit.Framework.TestAttribute
import OperatorViews.*
import ExplicitOperators.*
import kotlin.clr.byref

private infix fun Bits.and(right: Operand<Double>): Double = right.Value + 1.0

private sealed class NativeCase : Key(0) {
    object First : NativeCase()
    object Second : NativeCase()
}

class OperatorViewTests {
    @TestAttribute
    fun equalitySelectsTheTwoNativeOperatorsIndependently() {
        val left = Left()
        val right = Right<String>()
        check(left == right && left != right)
        check(42 == right && 41 != right)
        fun literal(rhs: Right<String>?) = 42 == rhs
        check(literal(right) && !literal(null))
        fun literalSubject(rhs: Right<String>?) = when (42) { rhs -> true; else -> false }
        check(literalSubject(right) && !literalSubject(null))
        val negated = !(left == right)
        check(!negated)
        val k = Key(3)
        val other = Key(3)
        fun nullSubject(rhs: Key?) = when (null) { rhs -> -1; else -> rhs.Number }
        check(nullSubject(k) == k.Number && nullSubject(null) == -1)
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
        check(maybe(k, other) && !maybe(k, null))
        check(k.Oblivious() == other)
        fun nullable(a: Key?, b: Key): Boolean {
            val before = a == b
            if (a == null) return !before
            return before && a == b && a != b
        }
        check(nullable(k, other) && nullable(null, other))
        var calls = 0
        fun next(): Key { calls++; return k }
        check(next() != next() && calls == 2)

        fun lifted(a: Key?, b: Key?): Boolean {
            val equal = a == b
            val unequal = a != b
            return if (a === null || b === null) equal == (a === b) && unequal == (a !== b)
                   else equal && unequal
        }
        Key.Trace = ""
        check(lifted(null, null) && lifted(k, null) && lifted(null, other) && Key.Trace == "")
        check(lifted(k, other) && Key.Trace == "=!")
        fun numberIfEqual(a: Key?, b: Key) = if (a == b) a.Number else -1
        fun numberUnlessDifferent(a: Key, b: Key?): Int {
            if (a != b) return -1
            return b.Number
        }
        check(numberIfEqual(k, other) == 3 && numberIfEqual(null, other) == -1)
        check(numberUnlessDifferent(k, other) == -1)
        fun classify(value: NativeCase?) = when (value) {
            null -> 0
            NativeCase.First -> 1
            NativeCase.Second -> 2
        }
        Key.Trace = ""
        check(classify(null) == 0 && classify(NativeCase.Second) == 1 && Key.Trace == "=")
        Key.Trace = ""
        check(Key.Observe("a", null) != Key.Observe("b", other) && Key.Trace == "ab")
        Key.Trace = ""
        val branch = when (Key.Observe("s", k)) {
            Key.Observe("a", null) -> 0
            Key.Observe("b", other) -> 1
            Key.Observe("c", k) -> 2
            else -> 3
        }
        check(branch == 1 && Key.Trace == "sab=")
        Key.Trace = ""
        val nullBranch = when (Key.Observe("s", null)) {
            Key.Observe("a", null) -> 0
            null -> 1
            else -> 2
        }
        check(nullBranch == 0 && Key.Trace == "sa")
        fun value(a: ValueKey?, b: ValueKey?): Boolean = a == b && a != b
        check(value(ValueKey(7), ValueKey(7)) && !value(ValueKey(7), null) && !value(null, null))
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
