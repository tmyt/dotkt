fun grade(score: Int): String = when (score) {
	0 -> "zero"
	in 1..59 -> "fail"
	else -> "pass"
}

fun sumRange(n: Int): Int {
	var s = 0
	for (i in 1..n) { s = s + i }
	return s
}

fun safeDiv(a: Int, b: Int): Int {
	try {
		return a / b
	} catch (e: ArithmeticException) {
		return -1
	}
}
