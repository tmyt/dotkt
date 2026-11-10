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
