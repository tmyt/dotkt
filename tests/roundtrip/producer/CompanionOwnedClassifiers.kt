package roundtrip.companionclassifiers

class Outer {
    class Token(val value: Int)

    fun readPrivate(): Int = Hidden(49).value
    fun readProtected(): Int = Restricted(53).value

    class Sibling {
        fun readPrivate(): Int = Hidden(50).value
    }

    companion object Named {
        class Token(val value: String)

        private class Hidden(val value: Int)
        protected class Restricted(val value: Int)
        val cachedPrivate: Int by lazy { Hidden(51).value }

        class WithFactory {
            companion object Factory {
                fun create(): Int = 43
            }
        }
    }
}

class GenericOuter<T> {
    fun readPrivate(): Int = Hidden(52).value

    companion object {
        private class Hidden(val value: Int)

        class Item<T>(val value: T) {
            class Nested<T>(val value: T) {
                companion object {
                    class Leaf(val value: Int)
                }
            }

            companion object Key {
                fun create(): Int = 44
            }
        }
    }
}

fun Outer.Named.WithFactory.Factory.answer(): Int = create() + 2

fun makeToken(): Outer.Named.Token = Outer.Named.Token("named")

fun readToken(token: Outer.Named.Token): String = token.value

fun tokens(): List<Outer.Named.Token> = listOf(makeToken())

fun directToken(): Int = Outer.Token(42).value

fun makeLeaf(): GenericOuter.Companion.Item.Nested.Companion.Leaf =
    GenericOuter.Companion.Item.Nested.Companion.Leaf(47)

fun readLeaf(leaf: GenericOuter.Companion.Item.Nested.Companion.Leaf): Int = leaf.value
