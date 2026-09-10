package constrainedcarrier

open class ReferencedSink<in T> {
    open fun <R : T> render(value: R): String = "base:$value"
    fun <R : T> echo(value: R): R = value
    fun <R : T> callback(value: R, render: (R) -> String): String = render(value)
}

class ReferencedSinkDerived : ReferencedSink<Any>() {
    override fun <R : Any> render(value: R): String = "derived:$value"
}

fun createReferencedSink(): ReferencedSink<Any> = ReferencedSinkDerived()

open class ReferencedAnimal
class ReferencedDog : ReferencedAnimal()

fun <T, R : System.Collections.Generic.IComparer<T>> compareReferencedGeneric(value: R, item: T): Int =
    value.Compare(item, item)
fun <T, R : System.Collections.Generic.IComparer<T>> deferReferencedGeneric(value: R, item: T): () -> Int =
    { value.Compare(item, item) }
