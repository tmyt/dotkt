fun main() {
	val xs = listOf(10, 20, 30)
	var sum = 0
	for (x in xs) { sum = sum + x }
	println("size = ${xs.size}")
	println("sum = $sum")
}
