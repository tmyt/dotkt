// #86 — an OVERLOADED generic .NET method whose declared return crosses.
//
// The chokepoint asks whether a stamp was made, not whether it said anything, so writing `void` for an overload set
// this could not narrow satisfied it and silently emptied the refusal's input. The call carries the exact
// `memberSig` the frontend resolved — the same descriptor ilemit links the overload by — so the return is resolved
// through it and the crossing is seen.
import ovgen.Fac

fun main() {
    val xs = Fac.Make<Int>(1)
    println(xs.Count)
}
