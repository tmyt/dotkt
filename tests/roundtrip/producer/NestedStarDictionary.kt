interface RemoteNestedStarKey<T> { fun read(): T }
class RemoteNestedStarIntKey : RemoteNestedStarKey<Int> { override fun read() = 9 }
class RemoteNestedStarDictionary {
    private val values = HashMap<RemoteNestedStarKey<*>, Any?>()
    fun put(key: RemoteNestedStarKey<*>, value: Any?) { values[key] = value }
    fun remove(key: RemoteNestedStarKey<*>): Any? = values.remove(key)
}
