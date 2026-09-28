package roundtrip.innerdefaults

class InnerDefaultOuter(val seed: Int) {
    var reads: Int = 0
    fun readSeed(): Int { reads++; return seed }

    inner class Middle(val offset: Int) {
        inner class Leaf(val value: Int = readSeed() + offset, val next: Int = value + 1) {
            val initialized: Int = seed + offset
            fun memberDefault(value: Int = this.value): Int = value
        }

        inner class Callback(val read: () -> Int = { readSeed() + offset })

        inner class Deeper(val extra: Int) {
            inner class Leaf(val value: Int = readSeed() + offset + extra)
        }
    }
}

class GenericInnerDefaultOuter<T>(val seed: T) {
    inner class Middle<U>(val middle: U) {
        inner class Callback(val read: () -> T = { seed })
        inner class Leaf(val value: T = seed, val own: U = middle) {
            fun afterConstruction(): T = seed
            fun memberDefault(value: T = this.value): T = value
        }
    }
}
