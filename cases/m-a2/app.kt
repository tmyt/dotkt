// A: control flow (when multi-value + ranges, do-while, labeled break, for) + increments (++ -- +=).
fun grade(n: Int): String = when (n) {
	0, 1, 2 -> "low"
	in 3..5 -> "mid"
	else -> "high"
}
fun main() {
	println(grade(1)); println(grade(4)); println(grade(9))
	var i = 0
	do { i++ } while (i < 3)
	println("do-while i=$i")
	outer@ for (a in 1..3) {
		for (b in 1..3) {
			if (a + b == 4) { println("break at $a,$b"); break@outer }
		}
	}
	var sum = 0
	for (x in 1..5) sum += x
	println("sum=$sum")
}
