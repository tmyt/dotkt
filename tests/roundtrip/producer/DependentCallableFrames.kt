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
