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

// #383 — CLR static storage belongs to each closed constructed generic type, so a companion carried INSIDE a generic
// owner would give `Host<Int>` and `Host<String>` a singleton each. Kotlin has no syntax that can tell those apart;
// C# does, which makes this consumer the only place the identity contract is observable. The private members exist
// because hoisting the carrier out of the owner costs it CLR nested access to them.
class BidirectionalGenericCompanionHost<T>(val value: T) {
    private val secret: Int = 4
    private fun hidden(): Int = 3

    companion object {
        var opened: Int = 0

        fun peek(host: BidirectionalGenericCompanionHost<Int>): Int = host.secret + host.hidden()
    }
}
