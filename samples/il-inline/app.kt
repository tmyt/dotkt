// IL parity: non-reified `inline fun` — reaches the backend as an ordinary function + call
// (we don't run FunctionInlining), which is semantically correct as long as there is no
// reified T, non-local return, or mutable-capture (those need the inlining spike).
inline fun twice(x: Int, f: (Int) -> Int): Int = f(f(x))
inline fun clamp(x: Int, lo: Int, hi: Int): Int = if (x < lo) lo else if (x > hi) hi else x

fun main() {
	println(twice(3) { it + 1 })
	println(twice(10) { it * 2 })
	println(clamp(5, 0, 3))
	println(clamp(-1, 0, 3))
}
