// #86 — a CONCRETE virtual property whose type crosses, overridden.
//
// The abstract accessor case is refused because an instantiable type must fill the slot at all; this one is only
// this type's problem because the author OVERRODE it, which is the arm that asks what the override physically
// states. A getter has no parameters, so that question is answered on the member's name and generic arity alone —
// which is the whole of a getter's CLR identity.
import plainnet.PropBase
import System.Collections.Generic.List

class CConcreteProp : PropBase() {
    override val Items: List<Int?> get() = List<Int?>()
}

fun main() {
    println(CConcreteProp().Tag.substring(0, 2))
}
