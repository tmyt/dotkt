package roundtrip.staticnested

interface OwnerBound
class OwnerValue : OwnerBound

open class StaticNestedOwner<T : OwnerBound>(private val value: T) {
    private class PrivateValue(val value: Int)
    protected class ProtectedValue(val value: Int)
    fun privateNestedValue(): Int = PrivateValue(45).value
    open class Failed {
        open fun marker(): Int = 41
    }
    class Closed : Failed() {
        override fun marker(): Int = 42
    }
    class Box<U>(val value: U) {
        class Leaf
        inner class Entry<V>(val item: V) {
            fun outer(): U = value
        }
    }
    class WithCompanion {
        companion object {
            fun value(): Int = 48
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

class StaticNestedDerived : StaticNestedOwner<OwnerValue>(OwnerValue()) {
    fun protectedNestedValue(): Int = ProtectedValue(46).value
}

fun makeStaticClosed(): StaticNestedOwner.Closed = StaticNestedOwner.Closed()
fun <U> makeStaticBox(value: U): StaticNestedOwner.Box<U> = StaticNestedOwner.Box(value)
fun makeStaticLeaf(): StaticNestedOwner.Box.Leaf = StaticNestedOwner.Box.Leaf()
fun echoStaticEntry(value: StaticNestedOwner.Box<String>.Entry<Int>): StaticNestedOwner.Box<String>.Entry<Int> = value
fun staticPrivateAccess(): Boolean {
    val value = OwnerValue()
    return StaticNestedOwner.Reader().read(StaticNestedOwner(value)) === value
}
