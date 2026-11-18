import ExplicitOperators.Counter
import ExplicitOperators.Ticket
import NUnit.Framework.TestAttribute
import System.Data.SqlTypes.SqlInt32

class ExplicitOperatorTests {
    @TestAttribute
    fun nativeResultsCheckedArithmeticAndMutableReceiversKeepTheirMeaning() {
        var evaluations = 0
        fun zero(): Counter { evaluations++; return Counter(0) }
        check(!Counter.op_True(zero()) && !Counter.op_False(zero()) && evaluations == 2)
        check(Counter.op_True(Counter(1)) && Counter.op_False(Counter(-1)))
        check(Counter.op_CheckedAddition(Counter(3), 2).Value == 5)
        val max = Counter(Int.MAX_VALUE)
        check((max + 1).Value == Int.MIN_VALUE)
        var overflow = false
        try { Counter.op_CheckedAddition(max, 1) } catch (_: System.OverflowException) { overflow = true }
        check(overflow)
        val nullResult = SqlInt32.op_Equality(SqlInt32.Null, SqlInt32.Null)
        check(nullResult.IsNull)
        var count = Counter(2)
        val unit: Unit = count.op_AdditionAssignment(3)
        check(unit == Unit && count.Value == 5)
        val ticket = Ticket(4)
        ticket.op_IncrementAssignment()
        check(ticket.Value == 5)
    }
}
