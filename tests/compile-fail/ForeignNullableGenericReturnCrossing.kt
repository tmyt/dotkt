// #86 — the RETURN half of the foreign crossing, which travels on a different channel from the parameter half.
//
// A node's own `ret` is the CALLER's Kotlin view of the result and is erased as a Kotlin slot — correctly, since it
// is what the value's Kotlin type is. So a C# `List<int?> Make()` reads as returning `List<object>` there and the
// crossing is invisible: the call compiled, was not refused, and left a `List<Nullable<Int32>>` on a stack typed as
// the unrelated Kotlin collection form. What the MEMBER declares has to be stamped beside its parameter vector, and
// read from there.
//
// The same channel carries a PROPERTY, whose declared type reaches it through the accessor — driven in the sibling
// case, since one refusal aborts the compilation and only the first is observable.
import fgn.Api

fun main() {
    val api = Api()
    println(api.OrElse(null, 7))     // control: a direct int? parameter still crosses
    println(api.Make())              // REFUSED: List<Nullable<Int32>> at a RETURN
}
