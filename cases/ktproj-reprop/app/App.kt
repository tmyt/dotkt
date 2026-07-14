// #17 APP: consumes the re-imported cross-module Kotlin type `kotlinx.cell.Cell` through a <ProjectReference>.
// A direct property GET (`c.value`), a direct property SET (`c.value = 42`), and a member fn that reads the
// property internally (`c.doubled()`) — all on a `kotlinx.`-packaged re-imported type. Before the #17 fix the
// GET/SET failed at ilemit ("method kotlinx.cell.Cell.value() not found"); now they lower to get_value/set_value.
import kotlinx.cell.Cell
import kotlinx.cell.makeCell

fun main() {
    val c = makeCell(10)
    println(c.value)        // 10   (cross-module property GET)
    c.value = 42            //      (cross-module property SET)
    println(c.value)        // 42
    println(c.doubled())    // 84   (member fn reading its own property)
}
