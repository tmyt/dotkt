package roundtrip.inneraccessors

class Cell<T>(val value: T)

interface Sink<T> { val context: Int }

open class Outer<X> {
    val storage = Cell<X?>(null)
    open inner class Body<T> : Sink<T> {
        override val context: Int get() = 17
    }
}

open class DeepOuter<X> {
    val storage = Cell<X?>(null)
    open inner class Middle<Y> {
        val middleStorage = Cell<Y?>(null)
        open inner class Body<T> : Sink<T> {
            override val context: Int get() = 29
        }
    }
}

interface Values<A, B> {
    val own: A
    val enclosing: B
}

open class TypedOuter<X>(val seed: X) {
    val storage = Cell<X?>(null)
    open inner class Body<T>(val item: T) : Values<T, X> {
        override val own: T get() = item
        override val enclosing: X get() = seed
    }
}

open class PlainOuter<X>(val seed: X) {
    open inner class Body<T>(val item: T) : Values<T, X> {
        override val own: T get() = item
        override val enclosing: X get() = seed
    }
}
