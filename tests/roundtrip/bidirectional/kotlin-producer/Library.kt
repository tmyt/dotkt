import BidirectionalInterop.Palette

class BidirectionalGreeter(val name: String) {
    fun greet(): String = "Hi, $name (accent=${Palette().Accent})"
    fun roster(): List<String> = listOf("$name A", "$name B", "$name C")
}

fun bidirectionalAdd(a: Int, b: Int): Int = a + b

// #251 — a nullable CONSTRUCTOR parameter must reach a C# consumer as [Nullable(2)], exactly like a nullable
// method parameter. The C# side asserts the emitted metadata by reflection (this consumer does not enable NRT,
// so a missing annotation would otherwise be invisible there).
class BidirectionalNullableCtor(val label: String?) {
    fun labelLength(): Int = label?.length ?: -1
    fun takeNullable(other: String?): Int = other?.length ?: -1

    // A CLR nested type's ctor param must be annotated too (a Kotlin consumer cannot name a nested DotKt class —
    // dll2klib does not surface one — so this axis is only assertable from C#).
    class Nested(val tag: String?) {
        fun tagLength(): Int = tag?.length ?: -1
    }
}
