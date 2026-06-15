class Point(val x: Int, val y: Int) {
	fun distanceSquared(): Int = x * x + y * y
	fun plus(other: Point): Point = Point(x + other.x, y + other.y)
	fun describe(): String = "($x, $y)"
}
