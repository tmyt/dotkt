// #100 H1 (reverse variance-collapse seam). A readonly-faced collection value flowing into a same-family
// collapsed MUTABLE type-arg slot. `make()` returns the readonly `List<Int>` head (ilemit: IReadOnlyList<int32>);
// the `Pair` ctor's collapsed type-arg slot and the `MutableMap<String, List<Int>>` value slot are the MUTABLE
// sibling (IList<int32>). Without ilemit's reverse-arm reconciling castclass the raw flow is StackUnexpected
// (readonly interface where the mutable one is expected). It is verifiable and succeeds at runtime because the
// concrete stdlib List<Int> implements every face.
fun make(): List<Int> = listOf(1, 2)

fun main() {
    val p = Pair(make(), 3)
    println(p)                              // ([1, 2], 3)
    val m = mutableMapOf<String, List<Int>>()
    m["k"] = make()
    println(m)                              // {k=[1, 2]}
}
