// Self-referential generic: Money implements IComparable<Money>. Passing a Money where IComparable<Money> is
// expected must resolve — the injector wires the self-argument via a lazy lookup-tag cone (no recursion).
import P.Money
import P.Cmp
fun main() {
    val a = Money(7); val b = Money(3)
    println(Cmp().Test(a, b))   // 7 - 3 = 4
}
