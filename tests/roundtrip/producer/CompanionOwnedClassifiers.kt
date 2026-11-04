package roundtrip.companionclassifiers

class Outer {
    class Token(val value: Int)

    companion object Named {
        class Token(val value: String)

        class WithFactory {
            companion object Factory {
                fun create(): Int = 43
            }
        }
    }
}

class GenericOuter<T> {
    companion object {
        class Item<T>(val value: T) {
            class Nested(val value: Int)

            companion object Key {
                fun create(): Int = 44
            }
        }
    }
}

fun Outer.Named.WithFactory.Factory.answer(): Int = create() + 2

fun makeToken(): Outer.Named.Token = Outer.Named.Token("named")

fun readToken(token: Outer.Named.Token): String = token.value

fun directToken(): Int = Outer.Token(42).value
