// IL parity: capturing lambdas (closures) — captured var becomes a closure-class field.
fun makeAdder(base: Int): (Int) -> Int = { x -> x + base }
fun applyN(f: (Int) -> Int, n: Int): Int = f(n)
fun main() {
    val add10 = makeAdder(10)
    val add100 = makeAdder(100)
    println(applyN(add10, 5))
    println(applyN(add100, 5))
    println(add10(7))
}
