// A concrete generic stdlib collection class (ArrayDeque<E> : AbstractMutableList<E>) used as a
// FIELD/owner type forces ilemit to reflectively resolve kotlin.collections.ArrayDeque`1 (and its
// AbstractMutableList base) from the rt dll. The base carries IList<E>.set_Item/RemoveAt and
// ICollection<E>.Add methodimpls whose Kotlin bodies return E/Boolean while the BCL slots return
// void — so ilemit must bridge them; a direct methodimpl makes the type unloadable ("cannot resolve").
class Holder {
    val q: ArrayDeque<String> = ArrayDeque()
}

fun main() {
    val h = Holder()
    val d = h.q
    d.addLast("a")
    d.addLast("b")
    d.addFirst("z")           // [z, a, b]
    println(d.removeFirst())  // z
    println(d.removeLast())   // b   -> [a]
    d.add("c")                // MutableCollection.add -> ICollection.Add (Boolean->void bridge)  [a, c]
    d[0] = "A"                // MutableList.set -> IList.set_Item (E->void bridge)                [A, c]
    println(d.removeAt(1))    // MutableList.removeAt -> IList.RemoveAt (E->void bridge): c        [A]
    println(d.size)           // 1
    println(d.first())        // A
}
