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
fun main() {
	println("sum 1..5 = ${sumRange(5)}")
	println("countdown 5 = ${countdown(5)}")
}
