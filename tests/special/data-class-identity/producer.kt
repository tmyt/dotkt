package roundtrip.dataidentity

data class Point(val x: Int = 3, val label: String = "original") {
    constructor(label: String) : this(label.length, label)
    var bodyState: Int = 17
    fun copy(tag: String, doubled: Int = x * 2): Int = tag.length + doubled
}

data class Box<T>(val value: T, val tag: String = "box")

data class Secret(private val value: Int) {
    fun reveal(): Int = value
}

data class FieldData(@kotlin.clr.ClrField val value: Int)

data class Named(@get:kotlin.clr.ClrName("readValue") val value: Int)

data class Hidden private constructor(val value: Int) {
    companion object {
        fun make(value: Int): Hidden = Hidden(value)
    }
}

class Container {
    data class Nested(val value: Int)
}

data object Token

// Generated-looking members do not give an ordinary declaration data identity.
class Ordinary(val x: Int) {
    operator fun component1(): Int = x
    fun copy(x: Int = this.x): Ordinary = Ordinary(x)
}
