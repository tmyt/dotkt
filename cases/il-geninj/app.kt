// P1-2: a constructed-generic member type (List<Item>) resolves to the injected open List<T> applied to Item,
// so chained access reaches the real members (façade-free).
import P.Bag
import P.Item

fun main() {
    val bag = Bag()
    bag.Items.Add(Item("a"))
    bag.Items.Add(Item("b"))
    println(bag.Items.Count)           // 2  — ICollection-style Count through List<Item>
    println(bag.Items.get(0).Name)     // a  — indexer get(Int): Item, then Item.Name
}
