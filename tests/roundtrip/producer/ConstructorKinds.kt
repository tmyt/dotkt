package roundtrip.constructorkinds

class PrimaryAndSecondary(val value: Int = 11) {
    constructor(text: String) : this(text.length)
}

class OnlySecondary {
    val value: Int
    constructor() : this(17)
    constructor(value: Int) { this.value = value }
}

class GenericConstructorOuter<T : CharSequence>(val value: T) {
    inner class Inner {
        val stored: T
        constructor() : this(value)
        constructor(value: T) { stored = value }
    }
}

annotation class ConstructorKindAnnotation(val value: Int)
