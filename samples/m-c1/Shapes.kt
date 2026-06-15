open class Shape(val name: String) {
	open fun area(): Int = 0
	fun label(): String = "$name area=${area()}"
}

class Rect(val w: Int, val h: Int) : Shape("rect") {
	override fun area(): Int = w * h
}
