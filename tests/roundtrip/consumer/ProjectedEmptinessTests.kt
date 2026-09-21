package roundtriptests.emptiness

import NUnit.Framework.TestAttribute
import roundtrip.emptiness.*

class ProjectedEmptinessTests {
    @TestAttribute fun importedCollectionCountAcceptsRawAndGenericCollections() {
        val raw = System.Collections.ArrayList()
        raw.Add(7)
        for (value in arrayOf<Any>(raw, listOf(7), setOf(9))) {
            check(importedCollectionCount(value as Collection<*>) == 1)
        }
        check(importedListUpcastCount(raw as List<*>) == 1)
        check(importedListUpcastCount(listOf(7, 9)) == 2)
    }

    @TestAttribute fun importedCountDoesNotReplaceKotlinEmptinessOverrides() {
        val value = ImportedEmptyOverride()
        check(importedCollectionCount(value) == 2)
        check(importedListUpcastCount(value) == 2)
        check(importedCollectionIsEmpty(value))
        check(value.calls == 1)
    }

    @TestAttribute fun importedFunctionsAcceptRawLists() {
        val list = System.Collections.ArrayList()
        val value: Any = list
        check(importedListIsEmpty(value))
        check(importedCollectionIsEmpty(value))
        check(importedProjectedIsEmpty(value as List<*>))
        list.Add(7)
        check(!importedListIsEmpty(value))
        check(!importedCollectionIsEmpty(value))
        check(!importedProjectedIsEmpty(value as List<*>))
    }

    @TestAttribute fun importedKotlinOverrideStillWinsOverCount() {
        val list = ImportedEmptyOverride()
        val value: Any = list
        check(list.size == 2)
        check((value as List<*>).isEmpty())
        check(importedCollectionIsEmpty(value))
        check(importedProjectedIsEmpty(value as List<*>))
        check(list.calls == 3)
    }
}
