private val Map<Int, Int>.firstProperty: Int get() = 1

@get:kotlin.clr.ClrName("otherProperty")
private val MutableMap<Int, Int>.firstProperty: Int get() = 2

private val Map<Int, Int>.otherProperty: Int get() = 3

fun main() = println(mapOf(1 to 1).firstProperty)
