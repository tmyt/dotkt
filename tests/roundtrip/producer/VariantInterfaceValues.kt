package variantinterfacevalues

interface Source<out T> {
    val item: T
    fun read(): T = item
}
class IntSource : Source<Int> { override val item: Int get() = 17 }
class TextSource : Source<String> { override val item: String get() = "producer" }
interface Sink<in T> { fun accept(value: T) }
class AnySink : Sink<Any> {
    var last = ""
    override fun accept(value: Any) { last = value.toString() }
}
fun <T> read(source: Source<T>): T = source.read()
fun <T> text(source: Source<T>): String = source.item.toString()
fun <T> write(sink: Sink<T>, value: T) = sink.accept(value)
fun source(): Source<Any> = IntSource()
fun <T, R> transform(values: Array<out T>, block: (T) -> R): R = block(values[0])
open class ValueBase<T>(val value: T)
class ValueFromSource<T>(source: Source<T>) : ValueBase<T>(source.item)
class BoundHolder<S : Source<String>>(val source: S)
fun <S : Source<String>> boundIdentity(source: S): S = source
open class GenericStorage<T>(val value: T)
class TextStorage : GenericStorage<Source<String>>(TextSource())
class NestedSource : Source<Source<String>> { override val item: Source<String> get() = TextSource() }
