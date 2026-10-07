package innerconstraint

class PairBound<A, B>
interface Dependent<E>
interface Marker { fun label(): String }
class Value<E> : Dependent<E>, Marker { override fun label(): String = "value" }
class Owner<T> {
    inner class Direct<F, E>(val value: E?) where E : PairBound<T, F>
    inner class Transitive<E, F>(val value: F) where E : T, F : Dependent<E>, F : Marker {
        fun label(): String = value.label()
    }
}

fun owner(): Owner<String> = Owner()
