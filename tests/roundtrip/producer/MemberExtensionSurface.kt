package roundtrip.memberextensionsurface

class ValueBox<T>(val value: T)
class TextContext(val suffix: String)
class NumberContext(val delta: Int)

class GenericMemberPropertyCarrier<T>(var value: Any?)

open class GenericMemberPropertyHost {
    inline val <reified T> GenericMemberPropertyCarrier<T>.memberMatches: Boolean
        get() = value is T

    @Suppress("UNCHECKED_CAST")
    var <T> GenericMemberPropertyCarrier<T>.ordinaryMemberValue: T?
        get() = value as T?
        set(value) { this.value = value }

    var <T> GenericMemberPropertyCarrier<T>.ordinaryMemberCount: Int
        get() = value as Int
        set(value) { this.value = value }
}

var topLevelCustomGetter: Int = 10
    get() = field + 1

var topLevelCustomSetter: Int = 20
    set(value) { field = value + 2 }

val topLevelComputed: Int get() = 33

// A receiverless field-backed property and an extension property may share a Kotlin source name. dll2klib must keep
// both declarations instead of treating the extension accessor's Property row as ownership of the public field.
lateinit var mixedRepresentationStatus: String
val String.mixedRepresentationStatus: Int get() = length

class PartialAccessorHolder {
    var customGetter: Int = 30
        get() = field + 3

    var customSetter: Int = 40
        set(value) { field = value + 4 }

    val computed: Int get() = 55
}

class MixedRepresentationHolder {
    lateinit var status: String
    val String.status: Int get() = length
}

open class ExtensionLibrary(private val offset: Int) {
    val ValueBox<Int>.label: String get() = "value=" + (value + offset)

    // A real operator may coexist with the member-extension property's indexed CLR Property row. dll2klib must not
    // reverse-project that row as a second synthetic get(ValueBox) operator.
    operator fun get(value: ValueBox<Int>): String = "operator=" + (value.value + offset)

    var ValueBox<Int>.scaled: Int
        get() = value * offset
        set(value) { last = value + offset }

    // Same source property, accessor role, and parameter count. A downstream compiler must use the frontend-resolved
    // context/extension signature rather than choosing by name/arity or by the physical accessor spelling.
    context(context: TextContext)
    val ValueBox<Int>.contextual: String get() = "text=" + (value + offset) + context.suffix

    context(context: NumberContext)
    val ValueBox<Int>.contextual: String get() = "number=" + (value + offset + context.delta)

    var last: Int = 0

    suspend fun ValueBox<Int>.fetch(): Int = value + offset

    protected suspend fun ValueBox<Int>.hidden(): Int = value * 100 + offset

    suspend fun useHidden(value: ValueBox<Int>): Int = value.hidden()
}

open class InheritedPropertyBase {
    open val inheritedValue: Int get() = 1
}

open class InheritedPropertyMiddle : InheritedPropertyBase()

class InheritedPropertyLeaf : InheritedPropertyMiddle() {
    override val inheritedValue: Int get() = 2
}

// AbstractCollection.size is a Kotlin `val` projected onto the CLR Count property. A downstream module must derive
// this class's new setter from the frontend-resolved getter property allocation, not from `set_size`.
class RemappedMutableProperty : AbstractCollection<Int>() {
    override var size: Int = 2
    override fun iterator(): Iterator<Int> = emptyList<Int>().iterator()
}

open class CovariantPropertyValue(val text: String)
class NarrowCovariantPropertyValue(text: String) : CovariantPropertyValue(text)

interface CovariantPropertySlot<T> {
    val covariantValue: T
}

class CovariantPropertyImplementation : CovariantPropertySlot<CovariantPropertyValue> {
    override val covariantValue: NarrowCovariantPropertyValue
        get() = NarrowCovariantPropertyValue("narrow")
}

interface CovariantExtensionPropertySlot<T> {
    val ValueBox<Int>.covariantExtensionValue: T
}

class CovariantExtensionPropertyImplementation : CovariantExtensionPropertySlot<CovariantPropertyValue> {
    override val ValueBox<Int>.covariantExtensionValue: NarrowCovariantPropertyValue
        get() = NarrowCovariantPropertyValue("extension-" + value)
}
