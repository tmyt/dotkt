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
private class NativeReadOnlyControl : System.Collections.ObjectModel.ReadOnlyCollection<Int>(
    System.Collections.Generic.List<Int>()
)

class IndirectCollectionTests {
    @TestAttribute
    fun indirectNativeFacesPreserveFramesDefaultsAndInheritedPolicy() {
        check(localIndirectCollection() == 2)
        val ints = ImportedCollection<Int>()
        check(ints.add(7))
        check(NativeFaces.CopyFirstAndAppend(ints, 7) == 7 && ints.adds == 2)
        val nullable = ImportedCollection<String?>()
        check(nullable.add(null))
        check(NativeFaces.CopyFirstAndAppend(nullable, null) == null && nullable.adds == 2)
        val value = Any()
        val objects = ImportedCollection<Any>()
        check(objects.add(value))
        check(NativeFaces.CopyFirstAndAppend(objects, value) === value && objects.adds == 2)
        val list = ImportedList<String?>()
        list.add("first")
        list.add(null)
        check(NativeFaces.IndexOf(list, null) == 1)
        check(NativeFaces.IsReadOnly(NativeReadOnlyControl()))
    }
}
