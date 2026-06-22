// A concrete generic .NET collection (List<Item>) is assignable to EVERY generic interface it implements —
// IEnumerable<T>, ICollection<T>, IList<T> — the everyday .NET case, façade-free. Members reached via an explicit
// (non-public) interface impl (ICollection<T>.IsReadOnly) are emitted as concrete stubs so this holds.
import P.Bag
import P.Item
import P.Sink
fun main() {
    val b = Bag(); b.Items.Add(Item("a")); b.Items.Add(Item("b"))
    val s = Sink()
    println(s.CountE(b.Items))   // 2 — as IEnumerable<Item>
    println(s.CountC(b.Items))   // 2 — as ICollection<Item>
    println(s.CountL(b.Items))   // 2 — as IList<Item>
}
