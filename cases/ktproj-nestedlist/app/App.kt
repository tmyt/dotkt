import mylib.Box
import mylib.State
import mylib.useNested
import mylib.boxOfList
import mylib.stateOfList
import mylib.boxOfMutable
import mylib.useNestedMutable

fun main() {
    // read-only nested list round-trips as List<T> — the value from boxOfList unifies with the Box<List<String>> slot.
    val b: Box<List<String>> = boxOfList(listOf("a", "b"))
    println(useNested(b))                 // 2

    val st: State<List<Int>> = stateOfList(listOf(1, 2, 3))
    println(st.value.size)                // 3

    // mutable nested list still surfaces as MutableList<T> (read/write split preserved).
    val mb: Box<MutableList<Int>> = boxOfMutable(mutableListOf(10, 20))
    println(useNestedMutable(mb))         // 3   (add duplicates v[0] -> [10, 20, 10])
    println(mb.v)                         // [10, 20, 10]
}
