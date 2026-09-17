package roundtrip.mapvariance

fun widenMap(values: Map<String, String>): Map<String, Any> = values
fun widenNestedMap(values: MutableMap<String, MutableList<String>>): Map<String, List<String>> = values
fun groupedMap(): Map<Int, List<String>> = listOf("a", "b").groupBy { it.length }

fun <M : MutableMap<String, Int>> fillBoundMap(values: M): M {
    values["filled"] = 23
    return values
}

class MapHolder(val value: Map<String, Any>)
fun storeMap(values: Map<String, String>): MutableList<Map<String, Any>> = mutableListOf(values)
fun isMap(value: Any): Boolean = value is Map<*, *>
@Suppress("UNCHECKED_CAST")
fun castMap(value: Any): Map<String, Any> = value as Map<String, Any>
