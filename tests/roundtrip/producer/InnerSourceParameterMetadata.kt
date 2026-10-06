package roundtrip.innersourceparameters

class Cell<T>(var value: T)
open class Node<N : Node<N>>(previous: N?) { val previous = Cell<N?>(previous) }
class Concrete : Node<Concrete>(null)
interface Marker<T> { fun read(): T }
class NullableMarker : Marker<Int?> { override fun read(): Int? = 17 }
class StringMarker : Marker<String> { override fun read(): String = "projected" }

class Owner<A> {
    val storage = Cell<A?>(null)
    inner class Bounded<B : Node<B>>(val item: B)
    inner class Marked<B : Marker<Int?>, C>(val item: B, val other: C)
    inner class Projected<B : Marker<out String>, C>(val item: B, val other: C)
    inner class Dependent<B : Marker<out C>, C>(val item: B) {
        fun read(): C = item.read()
    }
    inner class Covariant<out B>(val item: B)
    inner class Contravariant<in B>(private val action: (B) -> Unit) {
        fun accept(value: B) { action(value) }
    }
    inner class Transform<B>(private val action: (B) -> B) {
        fun apply(value: B): B = action(value)
    }
    inner class FunctionFactory<B>(private val factory: () -> ((B) -> B)) {
        fun apply(value: B): B = factory()(value)
    }
    inner class Middle<B> {
        val storage = Cell<B?>(null)
        inner class Leaf<out C>(val item: C)
        inner class Callback<C>(private val transform: (C) -> C) {
            fun apply(value: C): C = transform(value)
        }
    }
}
