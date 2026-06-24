import clr.List
fun main() {
	val xs = List<Int>()
	xs.add(10); xs.add(20); xs.add(30)
	println("count = ${xs.count}")
	println("first = ${xs[0]}, last = ${xs[2]}")
	xs[1] = 99
	println("sum = ${xs[0] + xs[1] + xs[2]}")
}
