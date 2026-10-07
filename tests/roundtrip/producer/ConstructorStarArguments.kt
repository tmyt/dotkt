package roundtrip.constructorstararguments

class ConstructorProjectionOwner<T>(val outerValue: T) {
    inner class Token<U>(val value: U) {
        fun outer(): T = outerValue
    }
}
