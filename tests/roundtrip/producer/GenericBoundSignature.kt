package roundtrip.genericboundsignature

interface BoundSink<T> { fun accept(value: T) }

class StringBoundSink : BoundSink<String> {
    var latest = ""
    override fun accept(value: String) { latest = value }
}

fun <T : Any, C : MutableCollection<in T>> selectedBound(destination: C, value: T): String {
    destination.add(value)
    return "collection"
}

fun <T : Any, C : BoundSink<T>> selectedBound(destination: C, value: T): String {
    destination.accept(value)
    return "sink"
}
