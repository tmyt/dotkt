@kotlin.clr.ClrName("samePhysicalName")
private fun Map<Int, Int>.left(): Int = 1

@kotlin.clr.ClrName("samePhysicalName")
private fun MutableMap<Int, Int>.right(): Int = 2

fun main() = println(mapOf(1 to 1).left())
