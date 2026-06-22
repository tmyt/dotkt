// A concrete generic .NET collection (List<Item>) is assignable to a generic interface parameter (IEnumerable<Item>)
// — the everyday "pass a List where IEnumerable is expected" case, façade-free.
import P.Bag
import P.Item
import P.Sink
fun main() {
    val b = Bag()
    b.Items.Add(Item("a")); b.Items.Add(Item("b")); b.Items.Add(Item("c"))
    println(Sink().Count(b.Items))   // 3 — List<Item> passed where IEnumerable<Item> is expected
}
