import roundtrip.enumeration.ReferencedMutableEnumerationSet
import roundtrip.enumeration.erasedEnumerationView
import roundtrip.enumeration.consumeErasedEnumerationView

private open class LocalMutableEnumerationSet<T>(private val storage: MutableList<T>) : AbstractMutableSet<T>() {
    override val size: Int get() = storage.size
    override fun iterator(): MutableIterator<T> = storage.iterator()
    override fun contains(element: T): Boolean = storage.contains(element)
    override fun containsAll(elements: Collection<T>): Boolean = storage.containsAll(elements)
    override fun add(element: T): Boolean = if (storage.contains(element)) false else storage.add(element)
}

private class LocalInheritedEnumerationSet : LocalMutableEnumerationSet<String>(mutableListOf("local"))

private class NamedEnumerationSet : LocalMutableEnumerationSet<String>(mutableListOf("named")) {
    fun GetEnumerator(): Int = 99
}

private class ReferencedInheritedEnumerationSet : ReferencedMutableEnumerationSet<String>(mutableListOf("referenced"))

fun stringMutableEnumerationSet(): Any = LocalMutableEnumerationSet(mutableListOf("a", "b"))
fun intMutableEnumerationSet(): Any = LocalMutableEnumerationSet(mutableListOf(1, 2))
fun nullableMutableEnumerationSet(): Any = LocalMutableEnumerationSet(mutableListOf<Int?>(null, 3))
fun localInheritedEnumerationSet(): Any = LocalInheritedEnumerationSet()
fun referencedInheritedEnumerationSet(): Any = ReferencedInheritedEnumerationSet()
fun namedEnumerationSet(): Any = NamedEnumerationSet()
fun authoredEnumeratorValue(value: Any): Int = (value as NamedEnumerationSet).GetEnumerator()

private fun <T> enumerateFromKotlin(values: Iterable<T>): List<T> {
    val result = mutableListOf<T>()
    for (value in values) result.add(value)
    return result
}

fun kotlinMutableEnumerationChecks(): Boolean {
    val strings: MutableSet<String> = LocalMutableEnumerationSet(mutableListOf("a", "b"))
    val ints: MutableSet<Int> = LocalMutableEnumerationSet(mutableListOf(1, 2))
    val nullable: MutableSet<Int?> = LocalMutableEnumerationSet(mutableListOf<Int?>(null, 3))
    val local: MutableSet<String> = LocalInheritedEnumerationSet()
    val referenced: MutableSet<String> = ReferencedInheritedEnumerationSet()
    return strings.contains("a") && ints.contains(1) && nullable.contains(null)
        && local.contains("local") && referenced.contains("referenced")
        && enumerateFromKotlin(strings) == listOf("a", "b")
        && enumerateFromKotlin(ints) == listOf(1, 2)
        && enumerateFromKotlin(nullable) == listOf<Int?>(null, 3)
        && enumerateFromKotlin(local) == listOf("local")
        && enumerateFromKotlin(referenced) == listOf("referenced")
}

@Suppress("UNCHECKED_CAST")
fun kotlinErasedSetIdentity(value: Any): Any = value as MutableSet<Any?>

@Suppress("UNCHECKED_CAST")
fun kotlinErasedSetConcreteIdentity(value: Any): Any = value as MutableSet<String>

private class ErasedEnumerationSetBox(val view: MutableSet<Any?>)

fun kotlinCrossModuleErasedSetChecks(value: Any, element: Any?): Boolean {
    val view = erasedEnumerationView(value)
    return view === value && consumeErasedEnumerationView(view, element) && view.contains(element)
}

@Suppress("UNCHECKED_CAST")
fun kotlinErasedSetChecks(value: Any, present: Any?, added: Any?): Boolean {
    val view = value as MutableSet<Any?>
    check(view === value && view.contains(present))
    val query = { view.contains(present) }
    check(query())
    check(ErasedEnumerationSetBox(view).view.contains(present))
    val array = arrayOf(view)
    check(array[0] === value && array[0].contains(present))
    check(view.containsAll(listOf(present)))
    check(view.add(added) && !view.add(added))
    check(view.contains(added))
    check(view.remove(added) && !view.remove(added))
    view.clear()
    return view.size == 0 && view.isEmpty()
}

@Suppress("UNCHECKED_CAST")
fun kotlinErasedSetNativeConsumer(value: Any): Boolean {
    val view = value as MutableSet<String>
    val copy = System.Collections.Generic.List<String>(view as System.Collections.Generic.IEnumerable<String>)
    return copy.Count == 2
}

@Suppress("UNCHECKED_CAST")
fun kotlinErasedMutableInterfaceChecks(): Boolean {
    val rawList: Any = mutableListOf("x")
    val list = rawList as MutableList<Any?>
    check(list.add("y") && list.set(0, "z") == "x")
    check(list.remove("y") && list[0] == "z" && list.size == 1)
    check((rawList as MutableCollection<Any?>).contains("z"))
    val rawMap: Any = mutableMapOf("k" to 1)
    val map = rawMap as MutableMap<Any?, Any?>
    check(map.put("k", 2) == 1)
    map["new"] = 3
    check(map.remove("new") == 3)
    return map["k"] == 2 && map.size == 1
}

@Suppress("UNCHECKED_CAST")
fun kotlinErasedMutableCastControls(): Boolean {
    val readonly: Any = object : AbstractSet<String>() {
        override val size: Int get() = 1
        override fun iterator(): Iterator<String> = listOf("a").iterator()
    }
    check(readonly as? MutableSet<Any?> == null)
    check(readonly !is MutableSet<*>)
    val none: Any? = null
    check(none as? MutableSet<Any?> == null && none as MutableSet<Any?>? == null)
    var nullRejected = false
    try { none as MutableSet<Any?> } catch (_: NullPointerException) { nullRejected = true }
    val wrong: Any = "not a set"
    check(wrong as? MutableSet<Any?> == null)
    var wrongRejected = false
    try { wrong as MutableSet<Any?> } catch (_: ClassCastException) { wrongRejected = true }
    return nullRejected && wrongRejected
}
