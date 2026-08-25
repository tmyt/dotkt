import BidirectionalInterop.Palette
import BidirectionalInterop.ReferenceConstrainedTarget
import BidirectionalInterop.RefLikeConstrainedTarget
import BidirectionalInterop.RepeatedGenericOuter.RepeatedGenericInner
import BidirectionalInterop.StructConstrainedTarget
import System.FlagsAttribute
import kotlin.clr.ClrEnum

@FlagsAttribute
@ClrEnum
enum class BidirectionalAccess(value: UInt) {
    NONE(0u),
    READ(1u),
    WRITE(4u),
    READ_WRITE(5u),
    HIGH(0x80000000u),
}

@Retention(AnnotationRetention.RUNTIME)
annotation class BidirectionalAccessMarker(val value: BidirectionalAccess)

@BidirectionalAccessMarker(BidirectionalAccess.READ_WRITE)
class BidirectionalAccessMarked

fun bidirectionalEnumDefault(value: BidirectionalAccess = BidirectionalAccess.WRITE): BidirectionalAccess = value
fun bidirectionalEnumOrdinal(value: BidirectionalAccess): Int = value.ordinal

class BidirectionalGreeter(val name: String) {
    fun greet(): String = "Hi, $name (accent=${Palette().Accent})"
    fun roster(): List<String> = listOf("$name A", "$name B", "$name C")
}

fun bidirectionalAdd(a: Int, b: Int): Int = a + b

open class BidirectionalPropertyBase(open var value: Int) {
    open fun get_value(): Int = value + 100
    open fun set_value(next: Int) { value = next + 100 }
}

interface BidirectionalPropertyInterface {
    var value: Int
    fun get_value(): Int
    fun set_value(next: Int)
}

// #389 — companion extensions are emitted as the released C# 14 static extension-member graph. Keeping two
// receivers with the same source member name proves that bir2cir partitions the physical containers by receiver;
// the generic member proves that method type parameters and constraints stay on the executable declaration.
class BidirectionalStaticAlpha
class BidirectionalStaticBeta
class BidirectionalStaticInitAlpha
class BidirectionalStaticInitBeta

private var bidirectionalComputedStorage: Int = 10
private var bidirectionalExtensionInitLog: String = ""
private fun recordBidirectionalExtensionInit(label: String): String {
    bidirectionalExtensionInitLog += label
    return label
}

companion fun BidirectionalStaticAlpha.answer(): Int = 42
companion fun BidirectionalStaticAlpha.answer(value: Int): Int = 40 + value
companion fun BidirectionalStaticBeta.answer(): Int = 84
companion fun <T : Comparable<T>> BidirectionalStaticAlpha.echo(value: T): T = value
companion inline fun BidirectionalStaticAlpha.compute(block: () -> Int): Int = block()
internal companion fun BidirectionalStaticAlpha.internalAnswer(): Int = 9
private companion fun BidirectionalStaticAlpha.privateAnswer(): Int = 11
companion val BidirectionalStaticAlpha.label: String get() = "alpha"
companion val BidirectionalStaticAlpha.marker: String = "m"
companion var BidirectionalStaticAlpha.counter: Int = 0
companion lateinit var BidirectionalStaticAlpha.later: String
companion const val BidirectionalStaticAlpha.code: Int = 17
companion var BidirectionalStaticAlpha.computed: Int
    get() = bidirectionalComputedStorage
    private set(value) { bidirectionalComputedStorage = value + 1 }
companion var BidirectionalStaticAlpha.restricted: Int = 1
    private set
companion val BidirectionalStaticBeta.label: String get() = "beta"
companion val BidirectionalStaticInitAlpha.initialized: String = recordBidirectionalExtensionInit("A")
companion val BidirectionalStaticInitBeta.initialized: String = recordBidirectionalExtensionInit("B")

class BidirectionalGenericStatic<T>
companion fun BidirectionalGenericStatic.genericAnswer(): Int = 389
companion var BidirectionalGenericStatic.genericCounter: Int = 0
companion fun <TReceiver0> BidirectionalGenericStatic.echoGeneric(value: TReceiver0): TReceiver0 = value
companion fun ReferenceConstrainedTarget.referenceConstraint(): String = "reference"
companion fun StructConstrainedTarget.structConstraint(): String = "struct"
companion fun RefLikeConstrainedTarget.refLikeConstraint(): String = "ref-like"
companion fun RepeatedGenericInner.repeatedGenericNames(): String = "nested-generic"
companion fun List.listAliasAnswer(): Int = 144

fun updateRestrictedCompanionProperty() {
    BidirectionalStaticAlpha.restricted = 2
}

fun bidirectionalCompanionExtensionInitializationOrder(): String {
    val second = BidirectionalStaticInitBeta.initialized
    val first = BidirectionalStaticInitAlpha.initialized
    return "$first:$second:$bidirectionalExtensionInitLog"
}

fun bidirectionalStaticCalls(): String =
    "${BidirectionalStaticAlpha.answer()}:${BidirectionalStaticAlpha.answer(2)}:${BidirectionalStaticBeta.answer()}:" +
        "${BidirectionalStaticAlpha.echo("ok")}:${BidirectionalStaticAlpha.compute { 7 }}:" +
        "${BidirectionalStaticAlpha.internalAnswer()}:${BidirectionalStaticAlpha.privateAnswer()}"

fun bidirectionalStaticPropertyCalls(): String {
    BidirectionalStaticAlpha.counter = 6
    BidirectionalStaticAlpha.later = "ready"
    BidirectionalStaticAlpha.computed = 20
    return "${BidirectionalStaticAlpha.label}:${BidirectionalStaticAlpha.marker}:${BidirectionalStaticAlpha.code}:" +
        "${BidirectionalStaticAlpha.counter}:${BidirectionalStaticAlpha.later}:${BidirectionalStaticAlpha.computed}:" +
        BidirectionalStaticBeta.label
}

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
