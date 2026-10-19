@file:Suppress("UNCHECKED_CAST")

package kotlin.enums

// One immutable list per actual CLR enum type, shared by entries and enumEntries<T>().
// Ephemeron keys allow a collectible enum assembly and its cached entries to be reclaimed together.
@kotlin.clr.ClrTypeAlias("System.Runtime.CompilerServices.ConditionalWeakTable")
private class EnumEntriesCache<K : Any, V : Any> {
    constructor()
    @kotlin.clr.ClrIntrinsic("GetOrCreateValue")
    fun get(key: K): V = error("CLR binding")
}

private class EnumEntriesCell public constructor() {
    var entries: Any? = null
}

private val enumEntriesCache = EnumEntriesCache<Any, EnumEntriesCell>()

@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Enter")
private fun enterEntriesCell(cell: Any): Unit = error("CLR binding")
@kotlin.clr.ClrIntrinsic("System.Threading.Monitor.Exit")
private fun exitEntriesCell(cell: Any): Unit = error("CLR binding")

@PublishedApi
internal fun cachedEnumEntries(typeToken: Any): Any? {
    val cell = enumEntriesCache.get(typeToken)
    enterEntriesCell(cell)
    try { return cell.entries } finally { exitEntriesCell(cell) }
}

@PublishedApi
internal fun <E : Enum<E>> getOrCreateEnumEntries(typeToken: Any, values: Array<E>): EnumEntries<E> {
    val cell = enumEntriesCache.get(typeToken)
    enterEntriesCell(cell)
    try {
        val cached = cell.entries
        if (cached !== null) return cached as EnumEntries<E>
        val created = ClrEnumEntries(values)
        cell.entries = created
        return created
    } finally { exitEntriesCell(cell) }
}

// CLR enums are value types, and aliases may occupy several declaration slots with equal bits.
// Do not use the common implementation's singleton reference identity or physical value as an ordinal.
// This platform implementation refines the sealed common interface without changing upstream common sources.
@Suppress("SEALED_INHERITOR_IN_DIFFERENT_MODULE")
private class ClrEnumEntries<E : Enum<E>>(private val values: Array<E>) :
    EnumEntries<E>, AbstractList<E>(), RandomAccess {
    private val first = HashMap<E, Int>()
    private val last = HashMap<E, Int>()

    init {
        for (index in values.indices) {
            val value = values[index]
            if (!first.containsKey(value)) first[value] = index
            last[value] = index
        }
    }

    override val size: Int get() = values.size

    override fun get(index: Int): E {
        checkElementIndex(index, values.size)
        return values[index]
    }

    override fun contains(element: E): Boolean = (element as Any?) != null && first.containsKey(element)
    override fun indexOf(element: E): Int = if ((element as Any?) == null) -1 else first[element] ?: -1
    override fun lastIndexOf(element: E): Int = if ((element as Any?) == null) -1 else last[element] ?: -1
}
