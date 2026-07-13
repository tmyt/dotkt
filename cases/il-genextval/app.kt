// #157: an inferred `val c = Interop.Cell(40)` over a facadegen-injected generic `Cell<T>` must construct
// `Cell<Int>` (the value type arg reified INVARIANTLY), so the C#-origin extension `Peek(this Cell<int>)`
// binds its `__self` receiver to the SAME `Cell<int32>` instantiation and reads the stored value. If the
// un-annotated (`oblivious`) type-variable ctor param biased inference to `Cell<Int?>`, the receiver would
// be `Cell<Nullable<int32>>` — a distinct, layout-incompatible generic — and `c.Peek()` would read garbage.
import Interop.*

fun main() {
    val c = Interop.Cell(40)
    println(c.V)        // 40   (field read of the reified value arg)
    println(c.Peek())   // 41   (extension bound to Cell<int32>: c.V + 1)
}
