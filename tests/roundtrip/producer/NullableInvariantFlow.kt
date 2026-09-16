package roundtrip.nullableinvariantflow

import kotlin.clr.ClrRef

class Box<T>(var value: T)

interface Exchange<T> {
    fun exchange(value: Box<T?>): Box<T?>
}

class StringExchange : Exchange<String> {
    override fun exchange(value: Box<String?>): Box<String?> = value
}

fun sameModule(value: Box<String?>): Box<String?> = StringExchange().exchange(value)
fun <T> exchangeIdentity(value: Exchange<T>): Exchange<T> = value
fun <A, B, C> exchangeThroughExtraFrame(value: Exchange<C>): Exchange<C> = exchangeIdentity<C>(value)
fun <T> nullableIdentity(value: Box<T?>): Box<T?> = value
fun <T> identity(value: Box<T>): Box<T> = value
fun <T> create(value: T?): Box<T?> = Box<T?>(value)
fun <T> replace(box: Box<T?>, value: T?) { box.value = value }

fun <T> nullableBodyOnly(value: T?): Boolean {
    val box = Box<T?>(value)
    return box.value == null
}
fun <T> forwardNullableBodyOnly(value: T?): Boolean = nullableBodyOnly<T>(value)

interface NullableDefault {
    fun <T> defaultIdentity(value: Box<T?>): Box<T?> = value
}
class InheritedNullableDefault : NullableDefault

interface NullableBodySlot {
    fun <T> isAbsent(value: T?): Boolean
    fun <A, B> bothAbsent(first: A?, second: B?): Boolean
    fun <T> writeAndObserve(value: T, first: ClrRef<T>, second: ClrRef<T>): Boolean
}
class NullableBodyImplementation : NullableBodySlot {
    override fun <T> isAbsent(value: T?): Boolean = nullableBodyOnly<T>(value)
    override fun <A, B> bothAbsent(first: A?, second: B?): Boolean =
        nullableBodyOnly<A>(first) && nullableBodyOnly<B>(second)
    override fun <T> writeAndObserve(value: T, first: ClrRef<T>, second: ClrRef<T>): Boolean {
        first.value = value
        val observed = Box<T?>(second.value)
        return observed.value == value
    }
}

interface KeyRoot<K> { val marker: Int; val key: K }
interface KeyChild<K> : KeyRoot<K> { override val key: K }
class KeyImpl<K>(override val key: K) : KeyChild<K> {
    override val marker: Int get() = 1
}

class NullableOwner<T>(val value: Box<T?>) {
    fun <U> view(selected: U): KeyRoot<U> = object : KeyRoot<U> {
        override val key: U = selected
        override val marker: Int get() = if (value.value == null) 0 else 1
    }
}

fun readLocalMarker(value: KeyRoot<String>): Int = value.marker

class Bound<T : Box<String?>>(val box: T)
fun <T : Box<String?>> readBound(value: T): String? = value.value

abstract class Receiver<T> {
    abstract fun echo(value: Box<T>): T
    abstract fun echo(value: T): T
    abstract fun accept(value: Box<T>)
}
class StringReceiver : Receiver<String>() {
    var last: String = ""
    override fun echo(value: Box<String>): String = "box:" + value.value
    override fun echo(value: String): String = "value:" + value
    override fun accept(value: Box<String>) { last = value.value }
}
