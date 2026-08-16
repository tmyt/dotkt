// #86 — an OVERLOADED generic .NET method whose declared return crosses.
//
// The chokepoint asks whether a stamp was made, not whether it said anything, so writing `void` for an overload set
// this could not narrow satisfied it and silently emptied the refusal's input. The call carries the exact
// frontend-resolved declaration parameters, so bir2cir selects the same scalar memberRef and sees the crossing.
import ovgen.Fac

fun main() {
    val xs = Fac.Make<Int>(1)
    println(xs.Count)
}
