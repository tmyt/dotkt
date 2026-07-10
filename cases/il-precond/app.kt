// IL parity: precondition/error family + top-level repeat inline loop (#73 M6/M7).
// Exercises the recognition moved from kotc BirEmitter into bir2cir: the require/check/error/TODO
// throw-or-condition synthesis and the `repeat(n){}` counter loop (n once, index 0..n-1, action inlined).
fun main() {
	val acc = IntArray(1)
	repeat(3) { i -> acc[0] = acc[0] + i }
	println(acc[0])                    // 3  (0 + 1 + 2, index 0..n-1, captured `acc`)
	require(acc[0] == 3)               // passes (no throw)
	check(acc[0] == 3)                 // passes (no throw)
	try { require(false) } catch (e: IllegalArgumentException) { println("req") }
	try { check(false) } catch (e: IllegalStateException) { println("chk") }
	try { error("boom") } catch (e: IllegalStateException) { println("err:${e.message}") }
	try { TODO() } catch (e: NotImplementedError) { println("todo") }
}
