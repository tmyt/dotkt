package roundtriptests.emptiness

import NUnit.Framework.TestAttribute
import roundtrip.emptiness.*

class ProjectedEmptinessTests {
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
