// #86 — the crossing at the implementing position, behind a PROPERTY accessor.
//
// A C# `List<int?> Items { get; }` is a virtual `get_Items` carrying IsSpecialName, and skipping every special-name
// member left this whole column unchecked: the Kotlin override emitted `get_Items() : List<object>` beside a slot
// declaring `List<Nullable<int32>>` and the type failed to load. The failure mode differs from the method case only
// in which member kind states the slot, which is exactly why it needs its own witness.
import plainnet.IProp
import System.Collections.Generic.List

class CAccessor : IProp {
    override val Items: List<Int?> get() = List<Int?>()
}

fun main() {
    println(CAccessor().toString().substring(0, 2))
}
