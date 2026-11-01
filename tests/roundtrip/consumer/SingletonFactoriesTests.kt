package roundtriptests.singletonfactories

import NUnit.Framework.TestAttribute
import roundtrip.singletonfactories.singletonInLibrary
import roundtrip.singletonfactories.singletonCopyInLibrary
import roundtrip.singletonfactories.singletonSetInLibrary

class SingletonFactoriesTests {
    @TestAttribute fun genericDllFactoriesPreserveNullableAndValueTypeElements() {
        val map = singletonInLibrary<String?, Int?>(null, null)
        check(map.size == 1 && map.containsKey(null) && map[null] == null)
        check(singletonInLibrary(7, "value")[7] == "value")
        check(singletonSetInLibrary(42).single() == 42)
        check(singletonSetInLibrary<String?>(null).single() == null)
        val source = linkedMapOf("key" to 1)
        val copy = singletonCopyInLibrary(source)
        source["key"] = 2
        check(copy["key"] == 1)
    }
}
