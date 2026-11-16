import NUnit.Framework.TestAttribute
import NativeGenericNullableSlots.*

private class NativeGenericIntSource(override val Item: Int?) : Source<Int?>
private class NativeGenericLongSource(override val Item: Long?) : DerivedSource<Long?>
private class NativeGenericTextSource(override val Item: String?) : Source<String?>
private class NativeGenericFixedSource(override val Item: Long?) : FixedSource
private class NativeGenericMutableSource(override var Item: Long?) : MutableSource<Long?>
private class NativeGenericBaseSource(override val Item: Int?) : BaseSource<Int?>()
private class NativeGenericMidBaseSource(override val Item: Int?) : MidSource<String, Int?>()
private class NativeGenericMutableMidBaseSource(override var Item: Long?) : MutableMidSource<Long?, String>()
private fun <T> readNativeGenericSource(value: Source<T>): T = value.Item

private class NativeCollectionReimplementation : System.Collections.Generic.List<Int>(), MutableCollection<Int> {
    var sizeReads = 0
    var containsCalls = 0
    var clearCalls = 0
    override val size: Int get() { sizeReads++; return Count }
    override fun isEmpty() = Count == 0
    override fun contains(element: Int): Boolean { containsCalls++; return false }
    override fun containsAll(elements: Collection<Int>) = elements.all { contains(it) }
    override fun iterator(): MutableIterator<Int> =
        (0 until Count).map { this[it] }.toMutableList().iterator()
    override fun add(element: Int): Boolean { Add(element); return true }
    override fun addAll(elements: Collection<Int>): Boolean {
        var changed = false
        for (element in elements) { add(element); changed = true }
        return changed
    }
    override fun remove(element: Int): Boolean = Remove(element)
    override fun removeAll(elements: Collection<Int>): Boolean {
        var changed = false
        for (element in elements) changed = remove(element) || changed
        return changed
    }
    override fun retainAll(elements: Collection<Int>): Boolean {
        var changed = false
        for (index in Count - 1 downTo 0) {
            if (!elements.contains(this[index])) { RemoveAt(index); changed = true }
        }
        return changed
    }
    override fun clear() { clearCalls++; Clear() }
}

private class InheritedNativeCollection : System.Collections.Generic.List<Int>()

class NativeGenericNullableSlotTests {
    @TestAttribute fun interfaceGetterClosesInItsErasedApplicationFrame() {
        val present = NativeGenericIntSource(42)
        val absent = NativeGenericIntSource(null)
        val zero = NativeGenericIntSource(0)
        check(present.Item == 42 && absent.Item == null && zero.Item == 0)
        val slot: Source<Int?> = present
        val empty: Source<Int?> = absent
        check(slot.Item == 42 && empty.Item == null)
        check(readNativeGenericSource(slot) == 42 && readNativeGenericSource(empty) == null)
        check(Api.ReadBoxed(present) == 42 && Api.ReadBoxed(absent) == null && Api.ReadBoxed(zero) == 0)

        val wide: Source<Long?> = NativeGenericLongSource(5_000_000_000L)
        val noWide: Source<Long?> = NativeGenericLongSource(null)
        check(readNativeGenericSource(wide) == 5_000_000_000L && readNativeGenericSource(noWide) == null)
        check(Api.ReadBoxed(wide) == 5_000_000_000L && Api.ReadBoxed(noWide) == null)

        val text: Source<String?> = NativeGenericTextSource("value")
        val noText: Source<String?> = NativeGenericTextSource(null)
        check(readNativeGenericSource(text) == "value" && readNativeGenericSource(noText) == null)
        check(Api.ReadText(text) == "value" && Api.ReadText(noText) == null)

        val fixed: FixedSource = NativeGenericFixedSource(5_000_000_000L)
        val noFixed: FixedSource = NativeGenericFixedSource(null)
        check(fixed.Item == 5_000_000_000L && noFixed.Item == null)
        check(Api.ReadFixed(fixed) == 5_000_000_000L && Api.ReadFixed(noFixed) == null)
    }

    @TestAttribute fun mutablePropertyPreservesItsTypedScalarAndNativeSlot() {
        val value = NativeGenericMutableSource(null)
        val slot: MutableSource<Long?> = value
        check(value.Item == null && slot.Item == null && Api.ReadMutable(value) == null)
        slot.Item = 5_000_000_000L
        check(value.Item == 5_000_000_000L && Api.ReadMutable(value) == 5_000_000_000L)
        Api.WriteMutable(value, 6_000_000_000L)
        check(value.Item == 6_000_000_000L && slot.Item == 6_000_000_000L)
        Api.WriteMutable(value, null)
        check(value.Item == null && slot.Item == null)
        value.Item = 0L
        check(slot.Item == 0L && Api.ReadMutable(value) == 0L)
    }

    @TestAttribute fun nativeBasePropertiesKeepTheirSelectedSlots() {
        val present = NativeGenericBaseSource(23)
        val absent = NativeGenericBaseSource(null)
        val slot: BaseSource<Int?> = present
        val empty: BaseSource<Int?> = absent
        check(present.Item == 23 && absent.Item == null)
        check(slot.Item == 23 && empty.Item == null)
        check(Api.ReadBase(present) == 23 && Api.ReadBase(absent) == null)

        val middle = NativeGenericMidBaseSource(31)
        val noMiddle = NativeGenericMidBaseSource(null)
        val inherited: BaseSource<Int?> = middle
        check(middle.Item == 31 && noMiddle.Item == null && inherited.Item == 31)
        check(Api.ReadBase(middle) == 31 && Api.ReadBase(noMiddle) == null)

        val mutable = NativeGenericMutableMidBaseSource(null)
        val mutableBase: MutableBaseSource<Long?> = mutable
        check(mutable.Item == null && mutableBase.Item == null && Api.ReadMutableBase(mutable) == null)
        mutableBase.Item = 5_000_000_000L
        check(mutable.Item == 5_000_000_000L && Api.ReadMutableBase(mutable) == 5_000_000_000L)
        Api.WriteMutableBase(mutable, 6_000_000_000L)
        check(mutable.Item == 6_000_000_000L && mutableBase.Item == 6_000_000_000L)
        Api.WriteMutableBase(mutable, null)
        check(mutable.Item == null && mutableBase.Item == null)

        val collection = NativeCollectionReimplementation()
        collection.Add(7)
        val native: System.Collections.Generic.ICollection<Int> = collection
        val readOnly: System.Collections.Generic.IReadOnlyCollection<Int> = collection
        check(native.Count == 1 && collection.sizeReads == 1)
        check(readOnly.Count == 1 && collection.sizeReads == 2)
        check(collection.size == 1 && collection.sizeReads == 3)
        val kotlin: MutableCollection<Int> = collection
        check(kotlin.size == 1 && collection.sizeReads == 4)
        check(!kotlin.contains(7) && collection.containsCalls == 1)
        val base: System.Collections.Generic.List<Int> = collection
        check(base.Count == 1 && collection.sizeReads == 4)
        check(base.Contains(7) && collection.containsCalls == 1)
        kotlin.clear()
        check(collection.clearCalls == 1 && base.Count == 0)

        val inheritedCollection = InheritedNativeCollection()
        inheritedCollection.Add(9)
        val inheritedValue: Any = inheritedCollection
        check((inheritedValue as MutableCollection<Int>).size == 1)
    }
}
