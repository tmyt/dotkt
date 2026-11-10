package roundtrip.internaldefaults

import kotlin.clr.ClrField
import kotlin.clr.ClrName

class InternalDefaultValue<T> internal constructor(val value: T)
class InternalDefaultMarker

class InternalDefaultOwner<T>(private val seed: T) {
    var reads: Int = 0
        private set
    @ClrName("internalRead")
    internal fun readInternal(): T { reads++; return seed }
    internal val property: T get() = seed
    @ClrField internal var field: T = seed
    internal fun <M> pick(value: M): M = value

    fun method(value: T = readInternal()): T = value
    fun property(value: T = property): T = value
    fun field(value: T = field): T = value
    fun reference(reader: () -> T = this::readInternal): T = reader()
    fun constructed(value: InternalDefaultValue<T> = InternalDefaultValue(seed)): InternalDefaultValue<T> = value
    fun <M> generic(value: M, result: M = pick(value)): M = result
    fun writeField(value: T) { field = value }
}

internal fun topInternal(value: Int): Int = value + 1
fun topDefault(value: Int = topInternal(41)): Int = value
fun sameModuleInternalDefault(): Int = InternalDefaultOwner(23).method()

interface InternalLeftBound
interface InternalRightBound
class InternalLeftValue : InternalLeftBound
class InternalRightValue : InternalRightBound
internal fun <T : InternalLeftBound> internalChoice(value: T): Int = 1
internal fun <T : InternalRightBound> internalChoice(value: T): Int = 2
fun leftDefault(value: InternalLeftValue, result: Int = internalChoice(value)): Int = result
fun rightDefault(value: InternalRightValue, result: Int = internalChoice(value)): Int = result

class InternalConstructedOverloads {
    val result: String
    internal constructor(value: List<Int?>) { result = "ints:" + value.size }
    internal constructor(value: List<Long?>) { result = "longs:" + value.size }
}
fun intConstructorDefault(value: InternalConstructedOverloads = InternalConstructedOverloads(listOf<Int?>(7, null))): String = value.result
fun longConstructorDefault(value: InternalConstructedOverloads = InternalConstructedOverloads(listOf<Long?>(7L, null))): String = value.result

class PublishedInternalDefaultOwner @PublishedApi internal constructor() {
    @PublishedApi @ClrField internal var publishedField: Int = 43
    @PublishedApi @ClrName("publishedDefaultTarget")
    internal fun value(): Int = publishedField
    fun method(result: Int = value()): Int = result
    fun reference(reader: () -> Int = this::value): Int = reader()
    fun field(result: Int = publishedField): Int = result
}
fun publishedConstructorDefault(value: PublishedInternalDefaultOwner = PublishedInternalDefaultOwner()): PublishedInternalDefaultOwner = value

open class LocalInternalDefaultBase {
    internal fun inherited(): Int = 31
    internal val inheritedProperty: Int get() = 37
}
class LocalInternalDefaultDerived : LocalInternalDefaultBase()
fun sameModuleInheritedDefaultAccess(): Int = LocalInternalDefaultDerived().let { it.inherited() + it.inheritedProperty }
