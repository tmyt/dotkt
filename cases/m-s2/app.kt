data class Point(val x: Int, val y: Int)

fun main() {
	val p = Point(3, 4)
	println(p.toString())
	val q = p.copy(x = 7, y = 9)
	println(q.toString())
	println("x=${p.component1()} y=${p.component2()}")
	val a = Point(1, 2)
	val b = Point(1, 2)
	val c = Point(3, 4)
	println("a==b: ${a == b}")
	println("a==c: ${a == c}")
	println("hash eq: ${a.hashCode() == b.hashCode()}")
}
