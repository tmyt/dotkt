private val reservedAccessorName: Int get() = 1

@kotlin.clr.ClrName("prop_get<reservedAccessorName>")
private fun collidesWithPropertyAccessor(): Int = 2

fun main() = println(reservedAccessorName + collidesWithPropertyAccessor())
