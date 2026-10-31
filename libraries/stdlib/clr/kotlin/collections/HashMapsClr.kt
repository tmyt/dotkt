@file:Suppress("UNCHECKED_CAST")

package kotlin.collections

import DotKt.Runtime.CompilerServices.kotlinEquals
import DotKt.Runtime.CompilerServices.kotlinHashCode

/** Hash storage accepts null through a non-null internal key, never an externally visible sentinel. */
private class ClrMapKey(val value: Any?) {
    override fun equals(other: Any?): Boolean = other is ClrMapKey && kotlinEquals(value, other.value)
    override fun hashCode(): Int = kotlinHashCode(value)
}

private class ClrMapNode(val key: Any?, var value: Any?) {
    var previous: ClrMapNode? = null
    var next: ClrMapNode? = null
}

/** One index and one entry chain own all operations, including every native dictionary facade. */
private class ClrMapCore(capacity: Int) {
    private val index = System.Collections.Generic.Dictionary<ClrMapKey, ClrMapNode>(capacity)
    var first: ClrMapNode? = null
        private set
    private var last: ClrMapNode? = null
    var version: Int = 0
        private set
    val size: Int get() = index.Count
    fun find(key: Any?): ClrMapNode? {
        val wrapped = ClrMapKey(key)
        return if (index.ContainsKey(wrapped)) index[wrapped] else null
    }
    fun put(key: Any?, value: Any?): Any? {
        val found = find(key)
        if (found != null) {
            val previous = found.value
            found.value = value
            return previous
        }
        val node = ClrMapNode(key, value)
        index.Add(ClrMapKey(key), node)
        node.previous = last
        if (last == null) first = node else last!!.next = node
        last = node
        version++
        return null
    }
    fun remove(node: ClrMapNode) {
        index.Remove(ClrMapKey(node.key))
        if (node.previous == null) first = node.next else node.previous!!.next = node.next
        if (node.next == null) last = node.previous else node.next!!.previous = node.previous
        version++
    }
    fun removeKey(key: Any?): Any? {
        val node = find(key) ?: return null
        remove(node)
        return node.value
    }
    fun clear() {
        if (size == 0) return
        index.Clear()
        first = null
        last = null
        version++
    }
    fun containsValue(value: Any?): Boolean {
        var node = first
        while (node != null) {
            if (kotlinEquals(node.value, value)) return true
            node = node.next
        }
        return false
    }
}

private class ClrMapEntry<K, V>(private val node: ClrMapNode) : MutableMap.MutableEntry<K, V> {
    override val key: K get() = node.key as K
    override val value: V get() = node.value as V
    override fun setValue(newValue: V): V {
        val old = node.value
        node.value = newValue
        return old as V
    }
    override fun equals(other: Any?): Boolean = AbstractMap.entryEquals(this, other)
    override fun hashCode(): Int = AbstractMap.entryHashCode(this)
    override fun toString(): String = key.toString() + "=" + value.toString()
}

// Query through Map.Entry: a read-only Map view can supply an entry that is not mutable.
// Every produced entry is mutable and add is unsupported, so the owning map's mutable view cast is safe.
private class ClrMapEntries<K, V>(private val core: ClrMapCore) : AbstractMutableSet<Map.Entry<K, V>>() {
    override val size: Int get() = core.size
    override fun add(element: Map.Entry<K, V>): Boolean =
        throw UnsupportedOperationException("Add is not supported on entries")
    override fun contains(element: Map.Entry<K, V>): Boolean {
        val node = core.find(element.key) ?: return false
        return kotlinEquals(node.value, element.value)
    }
    override fun remove(element: Map.Entry<K, V>): Boolean {
        val node = core.find(element.key) ?: return false
        if (!kotlinEquals(node.value, element.value)) return false
        core.remove(node)
        return true
    }
    override fun clear() = core.clear()
    override fun iterator(): MutableIterator<Map.Entry<K, V>> = ClrMapEntryIterator<K, V>(core)
}

private class ClrMapEntryIterator<K, V>(private val core: ClrMapCore) : MutableIterator<MutableMap.MutableEntry<K, V>> {
    private var nextNode = core.first
    private var lastNode: ClrMapNode? = null
    private var version = core.version
    private fun checkVersion() {
        if (version != core.version) throw ConcurrentModificationException()
    }
    override fun hasNext(): Boolean { checkVersion(); return nextNode != null }
    override fun next(): MutableMap.MutableEntry<K, V> {
        checkVersion()
        val node = nextNode ?: throw NoSuchElementException()
        nextNode = node.next
        lastNode = node
        return ClrMapEntry(node)
    }
    override fun remove() {
        checkVersion()
        val node = lastNode ?: throw IllegalStateException("next() has not been called")
        core.remove(node)
        version = core.version
        lastNode = null
    }
}

private fun clrMapString(map: Any, core: ClrMapCore): String {
    val result = StringBuilder("{")
    var node = core.first
    while (node != null) {
        if (node !== core.first) result.append(", ")
        result.append(if (node.key === map) "(this Map)" else clrElemToString(node.key))
        result.append("=")
        result.append(if (node.value === map) "(this Map)" else clrElemToString(node.value))
        node = node.next
    }
    return result.append("}").toString()
}

private fun checkMapLoadFactor(loadFactor: Float): Unit {
    require(loadFactor > 0 && !loadFactor.isNaN()) { "Load factor must be positive" }
}

@SinceKotlin("1.1")
public actual class HashMap<K, V> private constructor(private val core: ClrMapCore) : AbstractMutableMap<K, V>() {
    public actual constructor() : this(ClrMapCore(0))
    public actual constructor(initialCapacity: Int) : this(ClrMapCore(initialCapacity))
    public actual constructor(initialCapacity: Int, loadFactor: Float) : this(initialCapacity) { checkMapLoadFactor(loadFactor) }
    public actual constructor(original: Map<out K, V>) : this(original.size) { putAll(original) }
    actual override val size: Int get() = core.size
    actual override fun isEmpty(): Boolean = size == 0
    actual override fun containsKey(key: K): Boolean = core.find(key) != null
    actual override fun containsValue(value: V): Boolean = core.containsValue(value)
    actual override fun get(key: K): V? = core.find(key)?.value as V?
    actual override fun put(key: K, value: V): V? = core.put(key, value) as V?
    actual override fun remove(key: K): V? = core.removeKey(key) as V?
    actual override fun clear() = core.clear()
    actual override fun putAll(from: Map<out K, V>) { for ((key, value) in from) put(key, value) }
    actual override val keys: MutableSet<K> get() = super.keys
    actual override val values: MutableCollection<V> get() = super.values
    actual override val entries: MutableSet<MutableMap.MutableEntry<K, V>> = ClrMapEntries<K, V>(core) as MutableSet<MutableMap.MutableEntry<K, V>>
    override fun equals(other: Any?): Boolean = DotKt.Runtime.CompilerServices.structuralEquals(this, other, 2)
    override fun toString(): String = clrMapString(this, core)
}

@SinceKotlin("1.1")
public actual class LinkedHashMap<K, V> private constructor(private val core: ClrMapCore) : AbstractMutableMap<K, V>() {
    public actual constructor() : this(ClrMapCore(0))
    public actual constructor(initialCapacity: Int) : this(ClrMapCore(initialCapacity))
    public actual constructor(initialCapacity: Int, loadFactor: Float) : this(initialCapacity) { checkMapLoadFactor(loadFactor) }
    public actual constructor(original: Map<out K, V>) : this(original.size) { putAll(original) }
    actual override val size: Int get() = core.size
    actual override fun isEmpty(): Boolean = size == 0
    actual override fun containsKey(key: K): Boolean = core.find(key) != null
    actual override fun containsValue(value: V): Boolean = core.containsValue(value)
    actual override fun get(key: K): V? = core.find(key)?.value as V?
    actual override fun put(key: K, value: V): V? = core.put(key, value) as V?
    actual override fun remove(key: K): V? = core.removeKey(key) as V?
    actual override fun clear() = core.clear()
    actual override fun putAll(from: Map<out K, V>) { for ((key, value) in from) put(key, value) }
    actual override val keys: MutableSet<K> get() = super.keys
    actual override val values: MutableCollection<V> get() = super.values
    actual override val entries: MutableSet<MutableMap.MutableEntry<K, V>> = ClrMapEntries<K, V>(core) as MutableSet<MutableMap.MutableEntry<K, V>>
    override fun equals(other: Any?): Boolean = DotKt.Runtime.CompilerServices.structuralEquals(this, other, 2)
    override fun toString(): String = clrMapString(this, core)
}
