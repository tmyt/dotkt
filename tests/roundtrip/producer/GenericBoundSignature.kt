package roundtrip.genericboundsignature

interface BoundSink<T> { fun accept(value: T) }

class StringBoundSink : BoundSink<String> {
    var latest = ""
    override fun accept(value: String) { latest = value }
}

class ComparableBoundBox<T : Comparable<T>> {
    fun floor(value: T, minimum: T): T = value.coerceAtLeast(minimum)
}

fun <T : Any, C : MutableCollection<in T>> selectedBound(destination: C, value: T): String {
    destination.add(value)
    return "collection"
}

fun <T : Any, C : BoundSink<T>> selectedBound(destination: C, value: T): String {
    destination.accept(value)
    return "sink"
}
