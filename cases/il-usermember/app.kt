// #96: explicit hashCode()/toString()/equals() (and a bound method reference to them) on a user class/interface. A type
// that DECLARES the override must dispatch to the user member; a type that does NOT override inherits from kotlin.Any
// (== System.Object) and its universal call must reach the inherited slot by VIRTUAL dispatch — bir2cir AnySlotRebind
// rebinds the otherwise-dead-ending `callInstance <UserType>.GetHashCode/ToString/Equals` (base is the implicit
// kotlin.Any, so ilemit's FindMethod found no slot -> "method <UserType>.GetHashCode not found") to an `objMethod`, and
// retargets a dead-ending bound-delegate method reference's owner to kotlin.Any. A base-declared override reached
// through a non-declaring subclass must still resolve to the base member (left as the working callInstance).

class Point(val x: Int, val y: Int) {
	override fun hashCode(): Int = x * 31 + y
	override fun equals(other: Any?): Boolean = other is Point && other.x == x && other.y == y
	override fun toString(): String = "($x, $y)"
}

// No overrides -> inherits all three universal methods from kotlin.Any.
class Plain(val n: Int)

// Base declares only toString; the non-declaring subclass reaches the base's toString, but its hashCode falls to the
// inherited Object slot.
open class Base(val id: Int) { override fun toString(): String = "Base($id)" }
class Derived(id: Int) : Base(id)

// Interface-typed receiver: an overriding impl and a non-overriding impl.
interface Named { fun label(): String }
class WithName(val s: String) : Named {
	override fun label(): String = s
	override fun toString(): String = "WithName($s)"
}
class NoName : Named { override fun label(): String = "x" }

fun main() {
	val a = Point(1, 2)
	val b = Point(1, 2)
	val c = Point(3, 4)
	println(a.hashCode())                  // 33   (declared: 1*31+2)
	println(a.equals(b))                   // True (declared structural)
	println(a.equals(c))                   // False
	println(a == b)                        // True (== routes through declared equals)
	println(a.toString())                  // (1, 2)
	println(a)                             // (1, 2)  via println(Any?)

	val p = Plain(7)
	val q = Plain(7)
	println(p.hashCode() == p.hashCode())  // True  (stable inherited identity hash — no dead-end)
	println(p.equals(q))                   // False (inherited reference identity)
	println(p.equals(p))                   // True
	println(p == p)                        // True

	val d = Derived(9)
	println(d.toString())                  // Base(9)  (inherited declared toString)
	println(d.hashCode() == d.hashCode())  // True     (inherited Object.GetHashCode, stable)
	println(d)                             // Base(9)

	val n1: Named = WithName("hi")          // interface-typed receiver, overriding impl
	val n2: Named = NoName()                // interface-typed receiver, non-overriding impl
	println(n1.toString())                  // WithName(hi)
	println(n2.toString() == "NoName")      // True (inherited Object.ToString -> type name)
	println(n1.hashCode() == n1.hashCode()) // True
	println(n1.equals(n1))                  // True

	val hc: () -> Int = p::hashCode         // bound method reference to an inherited universal method
	println(hc() == p.hashCode())           // True (retargeted to System.Object::GetHashCode)
}
