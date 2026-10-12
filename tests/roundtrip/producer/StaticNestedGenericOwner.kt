package roundtrip.staticnested

interface OwnerBound
class OwnerValue : OwnerBound

class StaticNestedOwner<T : OwnerBound>(private val value: T) {
    open class Failed {
        open fun marker(): Int = 41
    }
    class Closed : Failed() {
        override fun marker(): Int = 42
    }
    class Box<U>(val value: U) {
        inner class Entry<V>(val item: V) {
            fun outer(): U = value
        }
    }
    class Reader {
        fun <T : OwnerBound> read(owner: StaticNestedOwner<T>): T = owner.value
    }
    object Singleton {
        fun marker(): Int = 43
    }
    enum class Choice { ONLY }
    inner class Captured {
        fun outer(): T = value
    }
}

fun makeStaticClosed(): StaticNestedOwner.Closed = StaticNestedOwner.Closed()
fun <U> makeStaticBox(value: U): StaticNestedOwner.Box<U> = StaticNestedOwner.Box(value)
fun staticPrivateAccess(): Boolean {
    val value = OwnerValue()
    return StaticNestedOwner.Reader().read(StaticNestedOwner(value)) === value
}
