package roundtrip.upstreamerasedstdlib

fun <K, V> emptyMapInLibrary(): Map<K, V> = emptyMap()

private object AuthoredEmptyMap : Map<Any?, Nothing> {
    override val size: Int get() = 0
    override fun isEmpty(): Boolean = true
    override fun containsKey(key: Any?): Boolean = false
    override fun containsValue(value: Nothing): Boolean = false
    override fun get(key: Any?): Nothing? = null
    override val keys: Set<Any?> get() = emptySet()
    override val values: Collection<Nothing> get() = emptyList()
    override val entries: Set<Map.Entry<Any?, Nothing>> get() {
        val bottom: Set<Nothing> = emptySet()
        return bottom
    }
}

@Suppress("UNCHECKED_CAST")
fun <K, V> authoredEmptyMapInLibrary(): Map<K, V> = AuthoredEmptyMap as Map<K, V>

fun <T : Comparable<T>> naturalOrderInLibrary(): Comparator<T> = naturalOrder()

fun <T : Comparable<T>> reverseOrderInLibrary(): Comparator<T> = reverseOrder()

fun <T> reversedInLibrary(comparator: Comparator<T>): Comparator<T> = comparator.reversed()

class ComparatorStorage<T>(val comparator: Comparator<T>)

class ComparatorProperty {
    val comparator: Comparator<String> = reverseOrder()
}

val storedComparator: Comparator<Int> = naturalOrder()

fun comparatorFactory(): () -> Comparator<Int> = { naturalOrder() }

fun <T : Comparable<*>> compareValuesInLibrary(left: T?, right: T?): Int = compareValues(left, right)

class OrderedValue(val rank: Int) : Comparable<OrderedValue> {
    override fun compareTo(other: OrderedValue): Int = rank - other.rank
}

enum class OrderedEntry { FIRST, SECOND }

interface JoinedValue<T> { fun select(value: T): Int }

private object BroadJoinedValue : JoinedValue<Any> {
    override fun select(value: Any): Int = 1
}

private class ConcreteJoinedValue<T> : JoinedValue<T> {
    override fun select(value: T): Int = 2
}

@Suppress("UNCHECKED_CAST")
fun <T> directJoinedValue(broad: Boolean): JoinedValue<T> =
    if (broad) BroadJoinedValue as JoinedValue<T> else ConcreteJoinedValue<T>()

@Suppress("UNCHECKED_CAST")
fun <T> nestedJoinedValue(branch: Int): JoinedValue<T> {
    val selected: JoinedValue<T> = when (branch) {
        0 -> BroadJoinedValue as JoinedValue<T>
        1 -> if (branch > 0) ConcreteJoinedValue<T>() else BroadJoinedValue as JoinedValue<T>
        else -> ConcreteJoinedValue<T>()
    }
    return selected
}

fun <T> concreteJoinedValue(first: Boolean): JoinedValue<T> =
    if (first) ConcreteJoinedValue<T>() else ConcreteJoinedValue<T>()

@Suppress("UNCHECKED_CAST")
fun <T> nullableJoinedValue(broad: Boolean): JoinedValue<T>? =
    if (broad) BroadJoinedValue as JoinedValue<T> else null

@Suppress("UNCHECKED_CAST")
fun <T> throwingJoinedValue(broad: Boolean): JoinedValue<T> =
    if (broad) BroadJoinedValue as JoinedValue<T> else error("missing joined value")

open class JoinedLocalUse {
    @Suppress("UNCHECKED_CAST")
    open fun select(broad: Boolean): Int {
        val selected: JoinedValue<Int> =
            if (broad) BroadJoinedValue as JoinedValue<Int> else ConcreteJoinedValue<Int>()
        return selected.select(42)
    }
}

class DerivedJoinedLocalUse : JoinedLocalUse() {
    @Suppress("UNCHECKED_CAST")
    override fun select(broad: Boolean): Int {
        val selected: JoinedValue<Int> =
            if (broad) BroadJoinedValue as JoinedValue<Int> else ConcreteJoinedValue<Int>()
        return selected.select(42)
    }
}

open class JoinedValueBase<T> {
    open fun select(first: Boolean): JoinedValue<T> = directJoinedValue(first)
}

class JoinedValueDerived<T> : JoinedValueBase<T>() {
    override fun select(first: Boolean): JoinedValue<T> =
        if (first) ConcreteJoinedValue<T>() else ConcreteJoinedValue<T>()
}

open class JoinedCarrierBase<T> {
    @Suppress("UNCHECKED_CAST")
    open fun select(): JoinedValue<T> = BroadJoinedValue as JoinedValue<T>
}

class JoinedCarrierDerived<T> : JoinedCarrierBase<T>() {
    @Suppress("UNCHECKED_CAST")
    override fun select(): JoinedValue<T> = BroadJoinedValue as JoinedValue<T>
}

open class JoinedArrayBase<T> {
    @Suppress("UNCHECKED_CAST")
    open fun values(broad: Boolean): Array<JoinedValue<T>> {
        val selected: JoinedValue<T> =
            if (broad) BroadJoinedValue as JoinedValue<T> else ConcreteJoinedValue<T>()
        return arrayOf(selected)
    }
}

class JoinedArrayDerived<T> : JoinedArrayBase<T>() {
    override fun values(broad: Boolean): Array<JoinedValue<T>> = arrayOf(ConcreteJoinedValue<T>())
}
