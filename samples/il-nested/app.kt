// IL parity: nested (non-inner) user classes, flattened to top-level synthetic types.
class Outer(val tag: String) {
	class Node(val v: Int) {
		fun describe(): String = "node($v)"
		class Leaf(val w: Int) { fun show(): String = "leaf $w" }
	}
	fun label(): String = "outer:$tag"
}
fun main() {
	println(Outer("root").label())
	val n = Outer.Node(7)
	println(n.describe())
	println(n.v * 2)
	println(Outer.Node.Leaf(3).show())
}
