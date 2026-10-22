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

fun defaultMembershipSubList(): Any = listOf("prefix", "value", "suffix").subList(1, 2)
