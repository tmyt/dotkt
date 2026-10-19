package roundtrip.inheritedarguments

class ArgumentBox<T>(val value: T)
open class ArgumentParent<T, R> {
    fun accept(value: ArgumentBox<T>): T = value.value
    fun acceptDefault(value: ArgumentBox<T>, other: R? = null): T = value.value
    fun <U> choose(value: ArgumentBox<T>, token: U): U = token
    fun ArgumentBox<T>.memberAccept(): T = value
}
class ArgumentChild<A, B> : ArgumentParent<B, A>()

fun <A, B> localArgumentForward(child: ArgumentChild<A, B>, value: ArgumentBox<B>): B = child.accept(value)
