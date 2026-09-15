package roundtrip.nullableinvariantflow

class Box<T>(var value: T)

interface Exchange<T> {
    fun exchange(value: Box<T?>): Box<T?>
}

class StringExchange : Exchange<String> {
    override fun exchange(value: Box<String?>): Box<String?> = value
}

fun sameModule(value: Box<String?>): Box<String?> = StringExchange().exchange(value)
fun <T> nullableIdentity(value: Box<T?>): Box<T?> = value
fun <T> identity(value: Box<T>): Box<T> = value
fun <T> create(value: T?): Box<T?> = Box<T?>(value)
fun <T> replace(box: Box<T?>, value: T?) { box.value = value }

interface KeyRoot<K> { val marker: Int; val key: K }
interface KeyChild<K> : KeyRoot<K> { override val key: K }
class KeyImpl<K>(override val key: K) : KeyChild<K> {
    override val marker: Int get() = 1
}

fun readLocalMarker(value: KeyRoot<String>): Int = value.marker
