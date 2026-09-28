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
private interface NestedStarSink<in T> { fun accept(value: T) }
private class NestedStarValueDictionary<V> {
    private val values = HashMap<NestedStarKey<*>, V>()
    fun put(key: NestedStarKey<*>, value: V) { values[key] = value }
    fun remove(key: NestedStarKey<*>): V? = values.remove(key)
}
private class NestedStarIntSink : NestedStarSink<Int> { override fun accept(value: Int) {} }
private class NestedStarWrappedDictionary {
    private val arrays = HashMap<Array<NestedStarKey<*>>, Any?>()
    private val functions = HashMap<String, (NestedStarKey<*>) -> Unit>()
    private val nullable = HashMap<NestedStarKey<*>?, Any?>()
    private val sinks = HashMap<NestedStarSink<*>, Any?>()
    fun putArray(key: Array<NestedStarKey<*>>, value: Any?) { arrays[key] = value }
    fun removeArray(key: Array<NestedStarKey<*>>): Any? = arrays.remove(key)
    fun putFunction(value: (NestedStarKey<*>) -> Unit) { functions["key"] = value }
    fun removeFunction(): ((NestedStarKey<*>) -> Unit)? = functions.remove("key")
    fun putNullable(key: NestedStarKey<*>?, value: Any?) { nullable[key] = value }
    fun removeNullable(key: NestedStarKey<*>?): Any? = nullable.remove(key)
    fun putSink(key: NestedStarSink<*>, value: Any?) { sinks[key] = value }
    fun removeSink(key: NestedStarSink<*>): Any? = sinks.remove(key)
}

class NestedStarDictionaryTests {
    @TestAttribute
    fun wrappedArgumentsAndContravariantKeysRemainUsable() {
        val values = NestedStarWrappedDictionary()
        val key: NestedStarKey<*> = NestedStarIntKey()
        val array = arrayOf(key)
        values.putArray(array, "array")
        check(values.removeArray(array) == "array")
        var seen = false
        values.putFunction { selected -> check(selected === key); seen = true }
        values.removeFunction()!!(key)
        check(seen)
        values.putNullable(key, "nullable")
        check(values.removeNullable(key) == "nullable")
        val sink: NestedStarSink<*> = NestedStarIntSink()
        values.putSink(sink, "sink")
        check(values.removeSink(sink) == "sink")
    }

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
        val ints = NestedStarValueDictionary<Int>()
        ints.put(first, 31)
        check(ints.remove(first) == 31)
        check(ints.remove(first) == null)
        val strings = NestedStarValueDictionary<String>()
        strings.put(second, "generic")
        check(strings.remove(second) == "generic")
        val nullable = NestedStarValueDictionary<Int?>()
        nullable.put(first, null)
        check(nullable.remove(first) == null)
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
