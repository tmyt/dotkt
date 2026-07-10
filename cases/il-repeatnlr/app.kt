// IL parity: NON-LOCAL return through `repeat(n){}` (#75 inline unification).
// `repeat` is an `inline` fun, so a bare `return` inside its lambda returns from the ENCLOSING function
// (non-local return), and a labeled `return@repeat` acts as `continue`. #73 M7 lowered repeat to a
// delegate-invoke loop in bir2cir, which dropped the non-local return; #75 carries the lambda body
// un-closured from kotc and SPLICES it, restoring both routings.
fun firstIndexHitting(target: Int): Int {
	repeat(10) { i ->
		if (i == target) return i        // NON-LOCAL return from firstIndexHitting
	}
	return -1
}

fun sumSkippingOdd(n: Int): Int {
	var s = 0
	repeat(n) { i ->
		if (i % 2 == 1) return@repeat    // labeled return = continue to next iteration
		s = s + i
	}
	return s
}

fun main() {
	println(firstIndexHitting(3))   // 3   (non-local return out of the loop)
	println(firstIndexHitting(99))  // -1  (loop completes, falls through)
	println(sumSkippingOdd(6))      // 6   (0 + 2 + 4, odd indices skipped via return@repeat)
	var acc = 0
	repeat(4) { acc += it }          // capture + implicit `it`
	println(acc)                    // 6   (0 + 1 + 2 + 3)

	// nested repeat: nested callInline hygiene (distinct loop vars, inner index resolves independently)
	var grid = 0
	repeat(3) { i -> repeat(2) { j -> grid = grid + i * 10 + j } }
	println(grid)                   // 63  (sum of i*10+j over i=0..2, j=0..1)

	// a scope function inside a repeat body must NOT destroy the outer index (`it`) binding
	var m2 = 0
	repeat(3) { val a = it.let { it + 1 }; m2 = m2 + a + it }
	println(m2)                     // 9   ((0+1)+0 + (1+1)+1 + (2+1)+2)
}
