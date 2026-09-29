package roundtrip.constructedmethods

class MethodBox<T>(val value: T)
class MethodOwner<T>(val owner: T) {
    fun <M> echo(input: M): M = input
    fun <M> pack(input: M): MethodBox<M> = MethodBox(input)
    fun <M> unpack(input: MethodBox<M>): M = input.value
    inner class Inner<U>(val own: U) {
        fun <M> echo(input: M): M = input
        fun <M> pack(input: M): MethodBox<M> = MethodBox(input)
        fun <M> unpack(input: MethodBox<M>): M = input.value
        fun <M> enclosing(input: M): T = owner
        fun <M> inner(input: M): U = own
    }
}

fun <A, B, M> checkLocalConstructedMethods(outer: A, own: B, input: M) {
    val owner = MethodOwner(outer)
    check(owner.echo(input) == input)
    check(owner.unpack(owner.pack(input)) == input)
    val child = owner.Inner(own)
    check(child.echo(input) == input)
    check(child.unpack(child.pack(input)) == input)
    check(child.enclosing(input) == outer && child.inner(input) == own)
}
