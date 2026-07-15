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
    // NB: a DIRECT `println(mb.v)` mis-prints the BCL List`1 (a separate cross-module generic-member
    // surface-type gap, tracked as #33). Assigning through the typed local surfaces MutableList<Int> and
    // Kotlin-formats correctly — which is what #29 (the read/write identity round-trip) asserts here.
    val mv: MutableList<Int> = mb.v
    println(mv)                           // [10, 20, 10]
}
