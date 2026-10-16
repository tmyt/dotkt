package roundtriptests.inheritedabstractcollections

import NUnit.Framework.TestAttribute
import roundtrip.inheritedabstractcollections.*

private class ImportedSet : LibrarySet()
private class ImportedList<T>(elements: List<T>) : LibraryList<T>(elements)
private class ImportedBase : LibraryBase()

class InheritedAbstractCollectionTests {
    @TestAttribute
    fun importedAbstractSetBaseRetainsItsSourceArityAndOverrides() {
        val original = ImportedSet()
        val source: Set<Any?> = original
        check(!source.isEmpty())
        check(original.emptyCalls == 1)
        check(source.size == 1)
        check(source.contains("x"))
        check(source.iterator().next() == "x")
        check((original as CollectionTag).tag() == "set")
    }

    @TestAttribute
    fun importedGenericAbstractListPreservesElementFrames() {
        val integers: List<Int> = ImportedList(listOf(7, 9))
        check(integers[0] == 7 && integers[1] == 9)
        val nullable: List<String?> = ImportedList(listOf(null, "x"))
        check(nullable.size == 2)
        check(nullable[0] == null && nullable[1] == "x")
        val control = ImportedBase()
        check(control.value == null)
        check((control as CollectionTag).tag() == "base")
    }
}
