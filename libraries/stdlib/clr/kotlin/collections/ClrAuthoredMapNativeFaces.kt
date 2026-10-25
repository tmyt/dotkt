@file:Suppress("UNCHECKED_CAST")

package kotlin.collections

import DotKt.Runtime.CompilerServices.KotlinMapDefaultSlots
import DotKt.Runtime.CompilerServices.StarProjectionType
import DotKt.Runtime.CompilerServices.starProjectionRuntimeType

@kotlin.clr.ClrTypeAlias("System.Collections.DictionaryEntry")
private class ClrRawMapEntry(key: Any?, value: Any?)

@kotlin.clr.ClrTypeAlias("System.Collections.IEnumerator")
private interface ClrAuthoredRawEnumeratorFace {
    fun MoveNext(): Boolean
    fun Reset()
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Current") val Current: Any?
}

@kotlin.clr.ClrTypeAlias("System.Collections.IDictionaryEnumerator")
private interface ClrAuthoredDictionaryEnumeratorFace : ClrAuthoredRawEnumeratorFace {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Key") val Key: Any?
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Value") val Value: Any?
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Entry") val Entry: ClrRawMapEntry
}

private class ClrAuthoredDictionaryEnumerator(source: Any) : ClrAuthoredDictionaryEnumeratorFace {
    private val iterator = clrProjectedIterator<Map.Entry<*, *>>((source as KotlinMapDefaultSlots).dotktMapEntries())
    private var currentEntry: Map.Entry<*, *>? = null
    override fun MoveNext(): Boolean = try {
        if (!iterator.hasNext()) { currentEntry = null; false }
        else { currentEntry = iterator.next(); true }
    } catch (error: ConcurrentModificationException) {
        currentEntry = null
        throw IllegalStateException(error.message, error)
    }
    override fun Reset(): Unit = throw UnsupportedOperationException("Reset is not supported")
    private fun selected(): Map.Entry<*, *> = currentEntry ?: throw IllegalStateException("Enumerator has no current entry")
    override val Key: Any? get() = selected().key
    override val Value: Any? get() = selected().value
    override val Entry: ClrRawMapEntry get() = ClrRawMapEntry(Key, Value)
    override val Current: Any get() = Entry
}

public fun clrAuthoredMapRawEnumerator(source: Any): Any = ClrAuthoredDictionaryEnumerator(source)

@kotlin.clr.ClrTypeAlias("System.Collections.ICollection")
private interface ClrAuthoredRawCollectionFace {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Count") val Count: Int
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "IsSynchronized") val IsSynchronized: Boolean
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "SyncRoot") val SyncRoot: Any
    fun CopyTo(array: System.Array?, index: Int)
    fun GetEnumerator(): ClrAuthoredRawEnumeratorFace
}

private class ClrAuthoredRawEnumerator(source: Any) : ClrAuthoredRawEnumeratorFace {
    private val iterator = clrProjectedIterator<Any?>(source)
    private var current: Any? = null
    private var valid = false
    override fun MoveNext(): Boolean = try {
        valid = iterator.hasNext()
        current = if (valid) iterator.next() else null
        valid
    } catch (error: ConcurrentModificationException) {
        valid = false
        current = null
        throw IllegalStateException(error.message, error)
    }
    override fun Reset(): Unit = throw UnsupportedOperationException("Reset is not supported")
    override val Current: Any? get() {
        if (!valid) throw IllegalStateException("Enumerator has no current element")
        return current
    }
}

private fun checkedMapCopyArray(array: Any?, index: Int, count: Int): System.Array {
    if (array == null) throw System.ArgumentNullException("array")
    val target = array as System.Array
    require(target.Rank == 1 && target.GetLowerBound(0) == 0) { "A one-dimensional zero-based array is required" }
    if (index < 0) throw System.ArgumentOutOfRangeException("index")
    require(index <= target.Length && count <= target.Length - index) { "Destination array is too small" }
    return target
}

private class ClrAuthoredRawCollection(private val source: Any, private val owner: Any,
    private val element: StarProjectionType) : ClrAuthoredRawCollectionFace {
    override val Count: Int get() = DotKt.Runtime.CompilerServices.projectedCollectionCountErased(source)
    override val IsSynchronized: Boolean get() = false
    override val SyncRoot: Any get() = owner
    override fun GetEnumerator(): ClrAuthoredRawEnumeratorFace = ClrAuthoredRawEnumerator(source)
    override fun CopyTo(array: System.Array?, index: Int) {
        val target = checkedMapCopyArray(array, index, Count)
        require(target.starProjectionRuntimeType().getElementType()!!.isAssignableFrom(element)) { "Invalid destination array type" }
        val iterator = clrProjectedIterator<Any?>(source)
        var destination = index
        while (iterator.hasNext()) target.SetValue(iterator.next(), destination++)
    }
}

public fun clrAuthoredRawCollection(source: Any, owner: Any, element: Any): Any =
    ClrAuthoredRawCollection(source, owner, element as StarProjectionType)

public fun <K, V> clrAuthoredMapRawCopy(source: Any, array: Any?, index: Int, pairType: Any) {
    val slots = source as KotlinMapDefaultSlots
    val target = checkedMapCopyArray(array, index, slots.dotktMapSize())
    val element = target.starProjectionRuntimeType().getElementType()!!
    val dictionaryEntry = ClrRawMapEntry(null, null).starProjectionRuntimeType()
    val rawEntries = element == dictionaryEntry
    require(rawEntries || element.isAssignableFrom(pairType as StarProjectionType)) { "Invalid destination array type" }
    val iterator = clrProjectedIterator<Map.Entry<*, *>>(slots.dotktMapEntries())
    var destination = index
    while (iterator.hasNext()) {
        val entry = iterator.next()
        val value: Any = if (rawEntries) ClrRawMapEntry(entry.key, entry.value)
            else ClrAuthoredMapPair(entry.key as K, entry.value as V)
        target.SetValue(value, destination++)
    }
}
