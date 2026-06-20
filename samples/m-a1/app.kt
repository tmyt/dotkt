// A-1: exhaustive when(is) + smart cast, extension functions, arrays, default arguments.
sealed class Node
class Leaf(val v: Int) : Node()
class Branch(val n: Int) : Node()

fun describe(node: Node): Int = when (node) {   // exhaustive when over a sealed type
	is Leaf -> node.v                            // smart cast: node.v
	is Branch -> node.n * 10
}

fun Int.tripled(): Int = this * 3                // extension function on a .NET/Kotlin type

fun tag(s: String = "def"): String = "<" + s + ">"   // default argument

fun main() {
	val nodes = arrayOf<Node>(Leaf(2), Branch(5))    // array
	for (nd in nodes) println(describe(nd))
	println(7.tripled())
	println(tag())
	println(tag("hi"))
	println(nodes.size)
}
