package roundtrip.memberextensionsurface

class ValueBox<T>(val value: T)

open class ExtensionLibrary(private val offset: Int) {
    val ValueBox<Int>.label: String get() = "value=" + (value + offset)

    var ValueBox<Int>.scaled: Int
        get() = value * offset
        set(value) { last = value + offset }

    var last: Int = 0

    suspend fun ValueBox<Int>.fetch(): Int = value + offset

    protected suspend fun ValueBox<Int>.hidden(): Int = value * 100 + offset

    suspend fun useHidden(value: ValueBox<Int>): Int = value.hidden()
}
