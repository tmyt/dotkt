// A real .NET generic collection (System.Collections.Generic.List<Int>) consumed façade-free via facadegen's
// `import System.X` scan: `.Add`, `.Count`, and the `this[i]` indexer (get_Item/set_Item) resolve to the BCL type.
import System.Collections.Generic.List

fun main() {
	val list = List<Int>()
	list.Add(10)
	list.Add(20)
	list.Add(30)
	println("count = ${list.Count}")
	println("first = ${list[0]}, last = ${list[2]}")

	list[1] = 99
	var sum = 0
	var i = 0
	while (i < list.Count) {
		sum = sum + list[i]
		i = i + 1
	}
	println("sum after set = $sum")
}
