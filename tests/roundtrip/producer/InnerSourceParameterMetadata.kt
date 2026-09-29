package roundtrip.innersourceparameters

class Cell<T>(var value: T)
open class Node<N : Node<N>>(previous: N?) { val previous = Cell<N?>(previous) }
class Concrete : Node<Concrete>(null)

class Owner<A> {
    val storage = Cell<A?>(null)
    inner class Bounded<B : Node<B>>(val item: B)
    inner class Covariant<out B>(val item: B)
    inner class Contravariant<in B>(private val action: (B) -> Unit) {
        fun accept(value: B) { action(value) }
    }
    inner class Middle<B> {
        val storage = Cell<B?>(null)
        inner class Leaf<out C>(val item: C)
    }
}
