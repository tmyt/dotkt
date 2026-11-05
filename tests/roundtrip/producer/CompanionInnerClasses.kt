package roundtrip.companioninner

class NamedOwner {
    companion object Named {
        class Value(val value: Int) {
            inner class Capture {
                fun read(): Int = value
            }
            fun read(): Int = Capture().read()
        }

        class DeclarationOnly(val value: String) {
            inner class Capture {
                fun read(): String = value
            }
        }
    }
}

class GenericOwner<Unused> {
    companion object {
        class Value<T>(val value: T) {
            inner class Capture<U>(val other: U) {
                fun outer(): T = value
                fun own(): U = other

                inner class Leaf {
                    fun outer(): T = value
                    fun middle(): U = other
                }
            }
            fun read(): T = Capture(7).outer()
        }
    }
}

fun makeCapture(): GenericOwner.Companion.Value<String>.Capture<Int> =
    GenericOwner.Companion.Value("producer").Capture(42)

object ObjectOwner {
    class Value(val value: Int) {
        inner class Capture { fun read(): Int = value }
        fun read(): Int = Capture().read()
    }
}

interface InterfaceOwner {
    class DirectValue(val value: Int) {
        inner class Capture { fun read(): Int = value }
        fun read(): Int = Capture().read()
    }

    companion object {
        class Value(val value: Int) {
            inner class Capture { fun read(): Int = value }
            fun read(): Int = Capture().read()
        }
    }
}

enum class EnumOwner {
    ITEM;
    inner class Tag { fun label(): String = name + ordinal }

    class Value(val value: Int) {
        inner class Capture { fun read(): Int = value }
        fun read(): Int = Capture().read()
    }
}

annotation class AnnotationOwner {
    class Value(val value: Int) {
        inner class Capture { fun read(): Int = value }
        fun read(): Int = Capture().read()
    }
}
