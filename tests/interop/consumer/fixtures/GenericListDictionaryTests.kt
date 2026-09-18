import NUnit.Framework.TestAttribute

class KotlinDerivedOrderedDictionary : CollectionStorageInterop.DerivedOrderedDictionary()
class KotlinOrderedDictionaryWithList : CollectionStorageInterop.OrderedDictionaryWithList()

class GenericListDictionaryTests {
    private fun checkedList(value: Any): List<String> = value as List<String>
    private fun checkedMutableList(value: Any): MutableList<String> = value as MutableList<String>

    private fun rejectsList(value: Any) {
        check(value !is List<*>)
        check(value !is MutableList<*>)
        check(value as? List<*> == null)
    }

    @TestAttribute fun dictionaryEntryListIsAnIndependentContract() {
        val value: Any = CollectionStorageInterop.EntryListDictionary()
        check(value is List<*>)
        check(value as? List<*> === value)
    }

    @TestAttribute fun inheritedMapStorageDoesNotGrantListIdentity() {
        rejectsList(CollectionStorageInterop.DerivedOrderedDictionary())
    }

    @TestAttribute fun kotlinSubclassDoesNotTurnInheritedStorageIntoNominalList() {
        rejectsList(KotlinDerivedOrderedDictionary())
    }

    private fun rejectsMutableList(value: Any) {
        check(value !is MutableList<*>)
        check(value as? MutableList<*> == null)
    }

    @TestAttribute fun addedReadOnlyListDoesNotGrantMutableListFromMapStorage() {
        rejectsMutableList(CollectionStorageInterop.OrderedDictionaryWithList())
    }

    @TestAttribute fun kotlinSubclassRetainsOnlyTheAddedReadOnlyContract() {
        val value: Any = KotlinOrderedDictionaryWithList()
        check(value as? List<String> === value)
        rejectsMutableList(value)
    }

    @TestAttribute fun addedListContractSurvivesInheritedMapStorage() {
        val value: Any = CollectionStorageInterop.OrderedDictionaryWithList()
        check(value as? List<String> === value)
        check(checkedList(value) === value)
    }

    @TestAttribute fun readOnlyListContractSurvivesDictionaryStorage() {
        val value: Any = CollectionStorageInterop.ReadOnlyListDictionary()
        check(value as? List<String> === value)
        check(checkedList(value) === value)
    }

    @TestAttribute fun mutableListContractSurvivesDictionaryStorage() {
        val value: Any = CollectionStorageInterop.MutableListDictionary()
        check(value as? MutableList<String> === value)
        check(checkedMutableList(value) === value)
    }
}
