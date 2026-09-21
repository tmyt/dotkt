package roundtriptests.setarguments

import NUnit.Framework.TestAttribute
import SetArgumentInterop.IntSetAndList
import roundtrip.setarguments.*

class ProjectedSetArgumentTests {
    @TestAttribute fun nominalSetDoesNotAcquireItsDictionaryStorageFamily() {
        val source: Any = SetArgumentRoundtrip.SetAndDictionary()
        val view = forwardImportedSet(source as Set<*>)
        check(view.size == 1)
        check(view.iterator().next() == 7L)
        check(view.contains(7L))
    }

    @TestAttribute fun importedSetParameterKeepsItsFamily() {
        val source: Any = IntSetAndList<String>()
        check(importedSetSize(source as Set<*>) == 3)
    }

    @TestAttribute fun importedWideningReturnsALiveSet() {
        val source = IntSetAndList<String>()
        val erased: Any = source
        val view: Set<*> = forwardImportedSet(erased as Set<*>)
        check(view.size == 3 && view.contains(7))
        source.Add(13)
        check(view.size == 4 && view.contains(13))
    }

    @TestAttribute fun importedNullableSetBoundaryPreservesNull() {
        check(forwardImportedNullableSet(null) == -1)
        val source: Any = IntSetAndList<String>()
        check(forwardImportedNullableSet(source as Set<*>) == 3)
    }
}
