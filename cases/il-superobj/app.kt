// issue #14, Object-slot variant: `super.toString()`/`super.hashCode()`/`super.equals()` whose immediate super is
// kotlin.Any must reach the System.Object slot NON-virtually. kotc emits the faithful non-virtual callInstance
// (ownerType kotlin.Any, virtual:false, anySlot:true), but bir2cir substitutes the @ClrTypeAlias(System.Object) owner
// to a `clrInstance System.Object::ToString` and DROPS the non-virtual intent, and ilemit's EmitInstanceCall emits an
// unconditional `callvirt` for a reference owner -> the call re-dispatches to THIS class's override -> infinite
// recursion (stack overflow). XFAIL_RUN until bir2cir carries the non-virtual flag onto clrInstance + ilemit honors it.

class Rec {
	override fun toString(): String = "R:" + super.toString().substring(0, 0)   // super -> System.Object::ToString
}

fun main() {
	println(Rec().toString().startsWith("R:"))   // want: true  (super.toString() = the type name, substring(0,0) = "")
}
