// IL parity: lambda -> delegate (non-capturing), higher-order functions, function-type params.
fun apply2(f: (Int) -> Int, x: Int): Int = f(x)
fun twice(g: (Int) -> Int, x: Int): Int = g(g(x))
fun main() {
    println(apply2({ n -> n * 2 }, 21))
    println(twice({ n -> n + 1 }, 10))
}
