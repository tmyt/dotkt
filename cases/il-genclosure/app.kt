// A closure inside a generic function that CAPTURES a value whose type involves the enclosing type parameter T.
// On the CLR (reified generics) the synthesized closure class must be generic over T — else `gp:T` is unresolved
// when the class is emitted. Covers: capturing a T value, a (T)->Unit, a List<T>, and returning T from the closure.
fun <T> capVal(x: T) { val run = { println(x) }; run() }
fun <T> capFn(f: (T) -> Unit, x: T) { val run = { f(x) }; run() }
fun <T> capList(xs: List<T>) { val run = { for (e in xs) println(e) }; run() }
fun <T> capRet(x: T): T { val run = { x }; return run() }
fun main() {
	capVal(1)
	capFn({ y -> println("fn:$y") }, 2)
	capList(listOf(3, 4))
	println("ret:" + capRet(5))
}
