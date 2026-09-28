import NUnit.Framework.TestAttribute

private class ConsumerNestedStarDictionary {
    private val values = HashMap<RemoteNestedStarKey<*>, Any?>()
    fun put(key: RemoteNestedStarKey<*>, value: Any?) { values[key] = value }
    fun remove(key: RemoteNestedStarKey<*>): Any? = values.remove(key)
}
class CrossModuleNestedStarDictionaryTests {
    @TestAttribute
    fun referencedKeysWorkInProducerAndConsumerOwnedDictionaries() {
        val key: RemoteNestedStarKey<*> = RemoteNestedStarIntKey()
        val local = ConsumerNestedStarDictionary()
        local.put(key, "local")
        check(local.remove(key) == "local")
        val remote = RemoteNestedStarDictionary()
        remote.put(key, "remote")
        check(remote.remove(key) == "remote")
        check(key.read() == 9)
    }
}
