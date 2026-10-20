package roundtrip.consumer

import NUnit.Framework.TestAttribute
import roundtrip.collectionhash.*

class CollectionHashRoundtripTests {
    @TestAttribute
    fun opaqueGenericAndInlineHashCallsAgreeAcrossDlls() {
        val values = arrayOf(producerHashList(), producerHashMap(), producerHashSet())
        val peers = arrayOf<Any>(listOf(23, 42), mapOf(2 to "b", 1 to "a"), setOf(42, 23))
        for (index in values.indices) {
            check(values[index] == peers[index])
            check(hashInProducer(values[index]) == peers[index].hashCode())
            check(genericHashInProducer(values[index]) == peers[index].hashCode())
            check(inlineHashInProducer(values[index]) == peers[index].hashCode())
        }
    }

    @TestAttribute
    fun genericHashStorageRetainsItsComparerAcrossDlls() {
        val first: Any = listOf(mapOf(1 to setOf(23, 42)))
        val second: Any = listOf(mapOf(1 to setOf(42, 23)))
        check(producerKeyMap(first)[second] == "stored")
        val set = producerKeySet(first)
        check(set.contains(second))
        check(!set.add(second))
        check(set.remove(second))
    }

    @TestAttribute
    fun importedHashReadersKeepTheKotlinObjectSlotMeaning() {
        val first = producerHashList()
        val second: Any = listOf(23, 42)
        check(producerHashReader(first)() == producerHashReader(second)())
        check(producerHashReader(first)() == second.hashCode())
    }
}
