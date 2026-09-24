package inheritedcovariantreference

interface Sink<in T> { fun send(value: T) }
interface Channel<T> : Sink<T> { val last: T }
interface Producer<in T> { val channel: Sink<T> }
open class Base<T>(private var value: T) : Channel<T> {
    override fun send(value: T) { this.value = value }
    override val last: T get() = value
    val channel: Channel<T> get() = this
}

open class Value(val text: String)
class Narrow(text: String) : Value(text)
interface Factory {
    val item: Value
    fun make(): Value
}
open class FactoryBase {
    open val item: Narrow get() = Narrow("base getter")
    open fun make(): Narrow = Narrow("base method")
}
