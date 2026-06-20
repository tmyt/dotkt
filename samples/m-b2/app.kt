// B (via b): scope functions apply / also / let / run / with, mapped to C# IIFEs.
class Box(var v: Int)
fun main() {
	val b = Box(1).apply { v = 10 }       // apply: returns receiver; `this`.v
	println(b.v)
	println(b.let { it.v + 5 })           // let: returns result; `it`
	println(with(b) { v * 2 })            // with: returns result; `this`
	val a = Box(3).also { it.v = 7 }      // also: returns receiver; `it`
	println(a.v)
	println(b.run { v + 1 })              // run: returns result; `this`
}
