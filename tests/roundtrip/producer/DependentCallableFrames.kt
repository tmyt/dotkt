package roundtrip.dependentframes

interface Marker<Tag>

open class Base<T> {
    var calls: Int = 0
    fun compare(other: T): Int {
        calls++
        return if (other === this) calls else -calls
    }
}

class Item : Base<Item>(), Marker<String>
class IntItem : Base<IntItem>(), Marker<Int>
class NullableItem : Base<NullableItem>(), Marker<Int?>

fun <Tag, T> constructFromMethod() where T : Base<T>, T : Marker<Tag> {
    Owner<Tag, T>()
}

class Owner<Tag, T> where T : Base<T>, T : Marker<Tag> {
    fun unbound(left: T, right: T): Int {
        val operation = Base<T>::compare
        return operation(left, right)
    }
    fun bound(left: T, right: T): Int {
        val operation = left::compare
        return operation(right)
    }
}

class Factory<Tag, T> where T : Base<T>, T : Marker<Tag> {
    fun constructOnly() { Owner<Tag, T>() }
}

class ConstructorCapture<Tag, T> where T : Base<T>, T : Marker<Tag> {
    val label: String
    init {
        val operation = Base<T>::compare
        label = operation.toString()
    }
}
