package roundtrip.indirectcollection.consumer

import NUnit.Framework.TestAttribute
import CollectionFaceInterop.NativeFaces
import roundtrip.indirectcollection.CollectionContract
import roundtrip.indirectcollection.ListContract
import roundtrip.indirectcollection.IndirectCollection
import roundtrip.indirectcollection.IndirectList
import roundtrip.indirectcollection.localIndirectCollection

private class ImportedCollection<T> : CollectionContract<T> by IndirectCollection<T>()
private class ImportedList<T> : ListContract<T> by IndirectList<T>()
private class ImportedDefaultCollection<T> : CollectionContract<T> {
    override val values: MutableList<T> = mutableListOf()
    override var adds: Int = 0
    override val size: Int get() = values.size
    override fun isEmpty() = values.isEmpty()
    override fun contains(element: T) = values.contains(element)
    override fun containsAll(elements: Collection<T>) = values.containsAll(elements)
    override fun iterator() = values.iterator()
    override fun addAll(elements: Collection<T>) = values.addAll(elements)
    override fun remove(element: T) = values.remove(element)
    override fun removeAll(elements: Collection<T>) = values.removeAll(elements)
    override fun retainAll(elements: Collection<T>) = values.retainAll(elements)
    override fun clear() = values.clear()
}
private class NativeReadOnlyControl : System.Collections.ObjectModel.ReadOnlyCollection1<Int>(
    System.Collections.Generic.List<Int>()
)
private interface NativeOnlyContract : CollectionFaceInterop.NativeCollectionDefaults
private class NativeOnlyCollection : NativeOnlyContract {
    override fun iterator(): Iterator<Int> = listOf(17).iterator()
}

class IndirectCollectionTests {
    @TestAttribute
    fun indirectNativeFacesPreserveFramesDefaultsAndInheritedPolicy() {
        check(localIndirectCollection() == 2)
        val ints = ImportedCollection<Int>()
        check(ints.add(7))
        check(NativeFaces.CopyFirstAndAppend(ints as System.Collections.Generic.ICollection<Int>, 7) == 7 && ints.adds == 2)
        val defaults = ImportedDefaultCollection<Int?>()
        check(defaults.add(null))
        check(NativeFaces.CopyFirstAndAppend(defaults as System.Collections.Generic.ICollection<Int?>, null) == null)
        check(defaults.size == 2 && defaults.adds == 2)
        val nullable = ImportedCollection<String?>()
        check(nullable.add(null))
        check(NativeFaces.CopyFirstAndAppend(nullable as System.Collections.Generic.ICollection<String?>, null) == null && nullable.adds == 2)
        val value = Any()
        val objects = ImportedCollection<Any>()
        check(objects.add(value))
        check(NativeFaces.CopyFirstAndAppend(objects as System.Collections.Generic.ICollection<Any>, value) === value && objects.adds == 2)
        val list = ImportedList<String?>()
        list.add("first")
        list.add(null)
        check(NativeFaces.IndexOf(list as System.Collections.Generic.IList<String?>, null) == 1)
        check(NativeFaces.IsReadOnly(NativeReadOnlyControl() as System.Collections.Generic.ICollection<Int>))
        val nativeOnly = NativeOnlyCollection() as System.Collections.Generic.ICollection<Int>
        check(NativeFaces.IsReadOnly(nativeOnly))
        check(nativeOnly.Contains(17))
    }
}
