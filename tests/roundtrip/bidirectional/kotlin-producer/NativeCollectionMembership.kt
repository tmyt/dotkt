class MembershipCallCounter {
    private var count: Int = 0
    fun record() { count++ }
    fun calls(): Int = count
}

private open class NativeMembershipSet<T>(
    private val needle: T,
    private val stored: T,
    private val counter: MembershipCallCounter,
) : AbstractSet<T>() {
    override val size: Int get() = 1
    override fun iterator(): Iterator<T> = listOf(stored).iterator()
    override fun contains(element: T): Boolean {
        counter.record()
        return element == needle
    }
}

private class NativeMembershipList<T>(
    private val needle: T,
    private val stored: T,
    private val counter: MembershipCallCounter,
) : AbstractList<T>() {
    override val size: Int get() = 1
    override fun get(index: Int): T {
        check(index == 0)
        return stored
    }
    override fun contains(element: T): Boolean {
        counter.record()
        return element == needle
    }
}

private class InheritedNativeMembershipSet(counter: MembershipCallCounter) :
    NativeMembershipSet<String>("base", "value", counter) {
    override fun contains(element: String): Boolean = super.contains(element) && element == "base"
}

fun stringMembershipSet(counter: MembershipCallCounter): Any = NativeMembershipSet("needle", "value", counter)
fun intMembershipSet(counter: MembershipCallCounter): Any = NativeMembershipSet(42, 7, counter)
fun nullableMembershipSet(counter: MembershipCallCounter): Any = NativeMembershipSet<Int?>(null, 7, counter)
fun stringMembershipList(counter: MembershipCallCounter): Any = NativeMembershipList("needle", "value", counter)
fun inheritedMembershipSet(counter: MembershipCallCounter): Any = InheritedNativeMembershipSet(counter)

fun throwingMembershipSet(failure: Throwable): Any = object : AbstractSet<String>() {
    override val size: Int get() = 1
    override fun iterator(): Iterator<String> = listOf("value").iterator()
    override fun contains(element: String): Boolean = throw failure
}

@Suppress("UNCHECKED_CAST")
fun kotlinMembership(value: Any, element: Any?): Boolean = (value as Collection<Any?>).contains(element)

fun defaultMembershipSet(): Any = object : AbstractSet<String>() {
    override val size: Int get() = 1
    override fun iterator(): Iterator<String> = listOf("value").iterator()
}

fun explicitDefaultMembershipSet(): Any = object : AbstractSet<String>() {
    private var depth: Int = 0
    override val size: Int get() = 1
    override fun iterator(): Iterator<String> = listOf("value").iterator()
    override fun contains(element: String): Boolean {
        check(depth++ == 0)
        try { return clrCollContains(this, element) } finally { depth-- }
    }
}

fun defaultMembershipSubList(): Any = listOf("prefix", "value", "suffix").subList(1, 2)

private class NativeMembershipMap(
    private val keysCounter: MembershipCallCounter,
    private val valuesCounter: MembershipCallCounter,
) : AbstractMap<String, String>() {
    override val entries: Set<Map.Entry<String, String>> get() = mapOf("Key" to "Value").entries
    override fun containsKey(key: String): Boolean {
        keysCounter.record()
        return key.equals("key", ignoreCase = true)
    }
    override fun containsValue(value: String): Boolean {
        valuesCounter.record()
        return value.equals("value", ignoreCase = true)
    }
}

fun authoredMembershipMap(keysCounter: MembershipCallCounter, valuesCounter: MembershipCallCounter): Any =
    NativeMembershipMap(keysCounter, valuesCounter)

@Suppress("UNCHECKED_CAST")
fun kotlinKeyMembership(value: Any, key: String): Boolean = (value as Map<String, String>).keys.contains(key)

@Suppress("UNCHECKED_CAST")
fun kotlinValueMembership(value: Any, item: String): Boolean = (value as Map<String, String>).values.contains(item)
