fun opaqueEquals(left: Any?, right: Any?): Boolean = left == right
fun opaqueHash(value: Any?): Int = value.hashCode()
fun projectedSize(value: Any): Int = (value as List<*>).size
fun projectedMutableSize(value: Any): Int = (value as MutableList<*>).size
