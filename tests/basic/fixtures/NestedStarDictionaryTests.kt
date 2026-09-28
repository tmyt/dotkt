import NUnit.Framework.TestAttribute

private interface NestedStarKey<T> { fun read(): T }
private class NestedStarIntKey : NestedStarKey<Int> { override fun read() = 7 }
private class NestedStarStringKey : NestedStarKey<String> { override fun read() = "key" }
private class NestedStarDictionary<T> {
    private val values = HashMap<NestedStarKey<*>, Any?>()
    fun put(key: NestedStarKey<*>, value: Any?) { values[key] = value }
    fun remove(key: NestedStarKey<*>): Any? = values.remove(key)
    inline fun removeInline(key: NestedStarKey<*>): Any? = remove(key)
}

class NestedStarDictionaryTests {
    @TestAttribute
    fun invariantNestedExistentialKeysKeepTheirDictionaryCarrier() {
        val values = NestedStarDictionary<Int>()
        val first = NestedStarIntKey()
        val second = NestedStarStringKey()
        values.put(first, 11)
        values.put(second, "value")
        check(values.remove(first) == 11)
        check(values.remove(second) == "value")
        check(values.remove(first) == null)
        check(first.read() == 7 && second.read() == "key")
    }

    @TestAttribute
    fun inlineUsesAndNullValuesRetainTheSameStorage() {
        val values = NestedStarDictionary<String>()
        val key: NestedStarKey<*> = NestedStarIntKey()
        val marker = NestedStarStringKey()
        values.put(key, marker)
        check(values.removeInline(key) === marker)
        values.put(key, null)
        check(values.removeInline(key) == null)
        val ordinary = HashMap<String, Any?>()
        ordinary["key"] = marker
        check(ordinary.remove("key") === marker)
    }
}
