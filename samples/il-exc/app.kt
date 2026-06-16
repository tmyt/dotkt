fun safeDiv(a: Int, b: Int): Int {
	try {
		return a / b
	} catch (e: ArithmeticException) {
		return -1
	}
}
fun main() {
	println("safeDiv(10,2) = ${safeDiv(10, 2)}")
	println("safeDiv(1,0) = ${safeDiv(1, 0)}")
}
