package roundtrip.innermemberdefaults

class MemberDefaultOuter(val seed: Int) {
    private val hidden: Int = seed + 100
    var reads: Int = 0
    fun readSeed(): Int { reads++; return seed }
    inner class Inner {
        fun value(value: Int = seed): Int = value
        fun counted(value: Int = readSeed()): Int = value
        fun callback(read: () -> Int = { readSeed() }): Int = read()
        fun hiddenDefault(value: Int = hidden): Int = value
        fun hiddenCallback(read: () -> Int = { hidden }): Int = read()
        @Suppress("NOTHING_TO_INLINE")
        inline fun inlineValue(): Int = seed
        inline fun withSeed(block: (Int) -> Int): Int = block(seed)
    }
}

class GenericMemberDefaultOuter<T>(val seed: T) {
    inner class Middle<U>(val middle: U) {
        fun pair(first: T = seed, second: U = middle): Pair<T, U> = Pair(first, second)
        inner class Leaf {
            fun outerValue(value: T = seed): T = value
            fun middleValue(value: U = middle): U = value
            fun callback(read: () -> T = { seed }): T = read()
            @Suppress("NOTHING_TO_INLINE")
            inline fun inlineValue(): T = seed
            inline fun withSeed(block: (T) -> T): T = block(seed)
        }
    }
}
