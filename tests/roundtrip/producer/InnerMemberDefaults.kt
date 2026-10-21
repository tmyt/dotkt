package roundtrip.innermemberdefaults

class MemberDefaultOuter(val seed: Int) {
    var reads: Int = 0
    fun readSeed(): Int { reads++; return seed }
    inner class Inner {
        fun value(value: Int = seed): Int = value
        fun counted(value: Int = readSeed()): Int = value
        fun callback(read: () -> Int = { readSeed() }): Int = read()
        @Suppress("NOTHING_TO_INLINE")
        inline fun inlineValue(): Int = seed
    }
}

class GenericMemberDefaultOuter<T>(val seed: T) {
    inner class Middle<U>(val middle: U) {
        inner class Leaf {
            fun outerValue(value: T = seed): T = value
            fun middleValue(value: U = middle): U = value
            fun callback(read: () -> T = { seed }): T = read()
            @Suppress("NOTHING_TO_INLINE")
            inline fun inlineValue(): T = seed
        }
    }
}
