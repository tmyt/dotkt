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

interface ProjectedMap<K, out V> : Map<K, V>

interface CovariantSource<out T> { fun read(): T }
interface ContravariantSink<in T> { fun accept(value: T) }

class CovariantTextMap<out V>(source: Map<String, V>) :
    ProjectedMap<String, V>, Map<String, V> by source

fun projectedTextMap(value: String): ProjectedMap<String, String> =
    CovariantTextMap(mapOf("present" to value))

fun defaultTextMap(): Map<String, String> = mapOf("present" to "one").withDefault { "fallback" }
