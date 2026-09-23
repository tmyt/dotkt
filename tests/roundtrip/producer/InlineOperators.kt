package roundtrip.inlineoperators

open class InlineOperator<T>(val value: T) {
    inline operator fun get(block: () -> T): T = block()
    inline operator fun get(index: Int, block: (T) -> T): T = block(value)
    inline operator fun set(index: Int, block: (T) -> Unit) { block(value) }

    inline operator fun get(crossinline block: () -> T, marker: String): T {
        return block()
    }

    inline operator fun get(noinline block: () -> T, marker: Boolean): T = block()
}

class InlineDefaultOperator {
    inline operator fun get(block: () -> Int, marker: Int = 17): Int = marker + block()
}

class InlineCrossOperator {
    inline operator fun get(crossinline block: () -> Int): Int {
        val invoke = { block() }
        return invoke()
    }
}
