// Producer half of the Kotlin 2.4 static-declaration round trip (#382). The consumer reads this through the BUILT
// assembly's projected KLIB, never through this source, so every declaration here is exercised exactly as a second
// module rediscovers it from metadata.
package roundtrip.companionstatics

class Counter(val n: Int) {
    companion {
        fun twice(x: Int): Int = x * 2
        fun twice(s: String): String = s + s
        val origin: Counter = Counter(0)
        var seen: Int = 1
        const val TAG: String = "counter"
    }

    // A real companion object on the SAME class: it must stay a distinct singleton across the round trip.
    companion object {
        val label: String = "real-companion"
        fun describe(): String = "obj:" + label
    }

    fun bump(): Int = n + 1
}

class Box<T>(val v: T) {
    companion {
        fun make(): String = "box"
        var count: Int = 0
    }
}

interface Shape {
    fun area(): Int
    companion {
        fun unitArea(): Int = 1
        val kind: String get() = "shape"
    }
}

class Tag(val label: String)

// Companion EXTENSIONS: receiverless statics associated with `Tag`, physically hosted by this file's facade class.
companion fun Tag.of(label: String): Tag = Tag(label)
companion val Tag.blank: Tag get() = Tag("")
companion val Tag.marker: String = "m"
companion var Tag.counter: Int = 0
