private fun Map<Int, Int>.samePhysicalName(): Int = 1
private fun MutableMap<Int, Int>.samePhysicalName(): Int = 2

fun main() = println(mapOf(1 to 1).samePhysicalName())
