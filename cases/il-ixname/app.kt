// Consume a .NET type whose indexer is custom-named via [IndexerName("Cell")]: the `g[i]` get / `g[i] = v` set must
// bind to get_Cell/set_Cell (resolved from the type's DefaultMemberAttribute), not the default get_Item/set_Item.
import PIx.Grid
fun main() {
    val g = Grid()
    println(g[0])          // 10   -> get_Cell(0)
    println(g[2])          // 30   -> get_Cell(2)
    g[1] = 99              // -> set_Cell(1, 99)
    println(g[1])          // 99   -> get_Cell(1)
}
