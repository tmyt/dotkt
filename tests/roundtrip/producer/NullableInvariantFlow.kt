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
fun <T> nullableLocalIdentity(value: Box<T?>): Box<T?> {
    fun local(input: Box<T?>): Box<T?> = input
    return local(value)
}

class NullableLocalOwner<T> {
    fun <U> choose(first: Box<T?>, second: Box<U?>): Box<U?> {
        fun local(input: Box<U?>): Box<U?> {
            first.value = first.value
            return input
        }
        return local(second)
    }
}
fun <T> identity(value: Box<T>): Box<T> = value
fun <T> create(value: T?): Box<T?> = Box<T?>(value)
fun <T> replace(box: Box<T?>, value: T?) { box.value = value }

inline fun <A, B, T> inlineNullableTransform(box: Box<T?>, transform: (T?) -> T?): Box<T?> {
    replace<T>(box, transform(box.value))
    return box
}

fun <T> throughInlineNullableFrame(box: Box<T?>): Box<T?> =
    inlineNullableTransform<String, Int, T>(box) { it }

fun <T> clearNullableElement(array: Array<T?>) { array[0] = null }
fun <T> nullableSupplier(seed: T?): () -> T? = { seed }

class NullableOwnerBody<T> {
    fun isAbsent(value: T?): Boolean = Box<T?>(value).value == null
}

fun <T> nullableBodyOnly(value: T?): Boolean {
    val box = Box<T?>(value)
    return box.value == null
}
fun <T> forwardNullableBodyOnly(value: T?): Boolean = nullableBodyOnly<T>(value)

interface NullableDefault {
    fun <T> defaultIdentity(value: Box<T?>): Box<T?> = value
}
class InheritedNullableDefault : NullableDefault

interface NullableConstrainedDefault {
    fun <T, U : Box<T?>> constrainedIdentity(value: U): U = value
}
class InheritedNullableConstrainedDefault : NullableConstrainedDefault

abstract class NullableGenericParent {
    abstract fun <T> echoNullable(value: Box<T?>): Box<T?>
}
interface NullableGenericSlot {
    fun <T> echoNullable(value: Box<T?>): Box<T?>
}
class NullableGenericChild : NullableGenericParent(), NullableGenericSlot {
    override fun <T> echoNullable(value: Box<T?>): Box<T?> = value
}

open class NullablePropertyParent<T>(initial: Box<T?>) {
    protected var current: Box<T?> = initial
}
class NullablePropertyChild<T>(initial: Box<T?>) : NullablePropertyParent<T>(initial) {
    fun exchange(next: Box<T?>): Box<T?> {
        val previous = current
        current = next
        return previous
    }
}

class InlineNullableBody {
    inline fun <T> isAbsent(value: T?, observe: () -> Unit): Boolean {
        observe()
        return Box<T?>(value).value == null
    }
}

fun <T> nullableFactory(): () -> Box<T?> = { Box<T?>(null) }
inline fun <T> inlineNullableFactory(): () -> Box<T?> = { Box<T?>(null) }
inline fun <reified T> nullableTypePredicate(): (Any?) -> Boolean = { it is T }
fun <T> nullableDeferred(box: Box<T?>): suspend () -> Box<T?> = { box }
suspend fun <T> nullableSuspendEcho(box: Box<T?>): Box<T?> = box
suspend fun <T> nullableAfterPause(box: Box<T?>, pause: suspend () -> Unit): Box<T?> {
    pause()
    return box
}
fun <T> scalarFromNullableBox(box: Box<T?>): T? = box.value
class NullableScalarHolder<T>(val box: Box<T?>) {
    fun scalar(): T? = box.value
}
class UnframedHelper<T>(val value: T)
interface NullableBodyDefault {
    fun <T> isAbsent(value: T?): Boolean = Box<T?>(value).value == null
}
class InheritedNullableBodyDefault : NullableBodyDefault
interface NullableSuspendBodySlot {
    suspend fun <T> isAbsent(value: T?): Boolean
}
class NullableSuspendBodyImpl : NullableSuspendBodySlot {
    override suspend fun <T> isAbsent(value: T?): Boolean = Box<T?>(value).value == null
}

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
