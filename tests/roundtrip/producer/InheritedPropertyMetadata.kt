package roundtrip.propertymetadata

class Box<T>(val item: T)

open class CarrierBase<T>(private val seed: List<T>) {
    val Value: Box<List<T>> get() = Box(seed)
}

class Outer<A>(val seed: A) {
    open inner class Base<B> {
        val Value: A get() = seed
    }
}
