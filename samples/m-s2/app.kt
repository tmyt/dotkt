data class Point(val x: Int, val y: Int)

fun main() {
	val p = Point(3, 4)
	println(p.toString())
	val q = p.copy(x = 7, y = 9)
	println(q.toString())
	println("x=${p.component1()} y=${p.component2()}")
}
