fun main() {
	val a = Point(3, 4)
	val b = Point(1, 2)
	val c = a.plus(b)
	println("c = ${c.describe()}")
	println("a.d2 = ${a.distanceSquared()}")

	val r = Rect(5, 6)
	println(r.label())
}
