package roundtrip.dependentnative

interface Marker<Tag> : RoundtripDependentNative.IMarker<Tag>
open class ForeignBoundBase<Tag> : RoundtripDependentNative.Base()
class Item<Tag> : Marker<Tag>
class Outer<Tag> { inner class Item : Marker<Tag> }

class Owner<Tag, T>(private val value: T) where T : Marker<Tag> {
    fun probe(expected: Any): Boolean = RoundtripDependentNative.Owner<Tag, T>() != null && value === expected
}

fun <Tag, T> make(value: T, expected: Any): Boolean where T : Marker<Tag> = Owner<Tag, T>(value).probe(expected)
fun <Tag, T> oneHop(value: T): Boolean where T : Marker<Tag> = make<Tag, T>(value, value)
