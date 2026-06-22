import P.Doc
import P.Item
fun main() {
    val d = Doc()
    d.Items.Add(Item("a"))        // Add inherited from ICollection<Item> via IList<Item> supertype chain
    d.Items.Add(Item("b"))
    println(d.Items.Count)        // 2  — Count (property) from ICollection<Item>
    println(d.Items.get(0).Name)  // a  — indexer get(Int): Item from IList<Item>
}
