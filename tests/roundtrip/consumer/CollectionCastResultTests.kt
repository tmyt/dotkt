import NUnit.Framework.TestAttribute
import collection.cast.results.*

class CollectionCastResultTests {
    @TestAttribute fun importedConcreteCastResultsKeepTheirInterfaceSignatures() {
        val list: Any = listOf<Any>("a")
        val set: Any = setOf<Any>("a")
        val mutable: Any = mutableSetOf<Any>("a")
        check(checkedCollection(list).size == 1)
        check(safeCollection(list) === list)
        check(checkedSet(set).size == 1)
        check(safeSet(set) === set)
        check(checkedMutableSet(mutable).size == 1)
        check(safeMutableSet(mutable) === mutable)
    }

    @TestAttribute fun importedExistentialCastResultsKeepTheirIdentity() {
        for (value in arrayOf<Any>(listOf(1), setOf(1), mutableSetOf(1))) {
            check(checkedExistentialCollection(value) === value)
            check(safeExistentialCollection(value) === value)
        }
    }
}
