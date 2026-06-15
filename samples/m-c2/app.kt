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

fun countdown(n: Int): String {
	var out = ""
	for (i in n downTo 1) { out = out + i }
	return out
}

fun safeDiv(a: Int, b: Int): Int {
	try {
		return a / b
	} catch (e: ArithmeticException) {
		return -1
	}
}

fun main() {
	println("grade(0)=${grade(0)} grade(30)=${grade(30)} grade(90)=${grade(90)}")
	println("sum 1..5 = ${sumRange(5)}")
	println("countdown 5 = ${countdown(5)}")
	println("safeDiv(10,2)=${safeDiv(10, 2)} safeDiv(1,0)=${safeDiv(1, 0)}")
}
