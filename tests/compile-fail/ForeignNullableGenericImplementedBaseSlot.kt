// #86 — the same crossing through an ABSTRACT BASE CLASS rather than an interface.
//
// The failure mode differed only in the CLR's wording ("does not have an implementation" rather than a signature
// mismatch), which is why it needs its own witness: the slot is unfillable for one reason and the refusal must not
// be keyed to the interface path that happened to be fixtured.
import plainnet.BTake
import System.Collections.Generic.List

class CB : BTake() {
    override fun Take(xs: List<Int?>): String = "B:ok"
}

fun main() {
    println(CB().toString().substring(0, 2))
}
