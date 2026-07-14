// issue #14 RESIDUAL, Object-slot variant: `super.toString()`/`super.hashCode()`/`super.equals()` whose immediate
// super is kotlin.Any must reach the System.Object slot NON-virtually. kotc emits the faithful non-virtual
// callInstance (ownerType kotlin.Any, virtual:false, anySlot:true, super:true); ObjectSlotRename renames the slot
// (ToString/GetHashCode/Equals) and MemberCallSubstitution substitutes the @ClrTypeAlias(System.Object) owner to a
// `clrInstance System.Object::…` — now CARRYING the `super` marker (was DROPPED), so ilemit emits a non-virtual
// `call` (a base-slot dispatch like C#'s `base.M()`). Without it the callvirt re-dispatches to THIS class's override
// -> infinite recursion (stack overflow). il-supercall covers the non-Object (user base / DIM) super path.

class Node(val id: Int) {
	override fun toString(): String = "N:" + super.toString().substring(0, 0) + id   // super -> System.Object::ToString
	override fun hashCode(): Int = super.hashCode()                                   // super -> System.Object::GetHashCode
	override fun equals(other: Any?): Boolean = super.equals(other)                   // super -> System.Object::Equals (identity)
}

fun main() {
	val a = Node(7); val b = Node(7)
	println(a.toString())                  // N:7  (super.toString() = type name, substring(0,0) = "")
	println(a.hashCode() == a.hashCode())  // True (stable identity hash; no recursion)
	println(a.equals(a))                   // True (reference identity via base Object.Equals)
	println(a.equals(b))                   // False (distinct instances)
}
