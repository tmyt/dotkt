// issue #185: an interface DEFAULT method (DIM) overridden by a GRANDCHILD, while the INTERMEDIATE class does NOT
// override, must dispatch to the grandchild's override (not the DIM default) through a base/interface reference.
// The grandchild's override is a fresh virtual slot unless ilemit wires a per-type MethodImpl to the interface slot
// (ECMA-335 II.12.2: most-derived MethodImpl wins over the DIM fallback).

interface Describable {
	val area: Int
	fun describe(): String = "shape area=$area"     // interface default method (DIM)
}

open class Shape(override val area: Int) : Describable       // does NOT override describe() — inherits the DIM

class Square(side: Int) : Shape(side * side) {
	override fun describe() = "square area=$area"            // grandchild override, intermediate did not override
}

// A DIRECT child overriding the DIM (already worked) — non-regression.
class Circle(override val area: Int) : Describable {
	override fun describe() = "circle area=$area"
}

// A THIRD level: intermediate does not override, next does, grandgrandchild refines again.
open class Poly(area: Int) : Shape(area)
class Pentagon(area: Int) : Poly(area) { override fun describe() = "pentagon area=$area" }

// Grandchild implementing an interface method inherited through the base class — the reverse-bridge GetEnumerator
// twin: an abstract base implements Iterable (its iterator() stays abstract -> no base GetEnumerator), the concrete
// subclass overrides iterator(). The subclass reaches Iterable only via its base, so GetEnumerator must be synthesized
// on the subclass (else `for` over it fails to load).
abstract class NumberBag(val nums: List<Int>) : Iterable<Int>
class OrderedBag(nums: List<Int>) : NumberBag(nums) {
	override fun iterator(): Iterator<Int> = nums.iterator()
}

fun main() {
	val s: Shape = Square(4)
	println(s.describe())                 // square area=16   (was: shape area=16)
	val d: Describable = Square(3)
	println(d.describe())                 // square area=9    (interface-typed dispatch)
	val plain: Shape = Shape(7)
	println(plain.describe())             // shape area=7     (still the DIM default — not overridden)
	val c: Describable = Circle(5)
	println(c.describe())                 // circle area=5    (direct child, non-regression)
	val p: Describable = Pentagon(11)
	println(p.describe())                 // pentagon area=11 (deeper base-class chain)
	var sum = 0
	for (x in OrderedBag(listOf(4, 5, 6))) sum += x
	println(sum)                          // 15  (Iterable inherited via the abstract base; GetEnumerator on the subclass)
}
