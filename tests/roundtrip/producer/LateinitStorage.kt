package roundtrip.lateinitstorage

class LateinitBox<T : Any> {
    lateinit var value: T
    fun initialized(): Boolean = this::value.isInitialized
    fun initializedByDefault(result: Boolean = this::value.isInitialized): Boolean = result
    fun other(other: LateinitBox<T>): Boolean = other::value.isInitialized
    fun reference(): kotlin.reflect.KMutableProperty0<T> = this::value
}

lateinit var exportedLateinit: String
fun exportedLateinitInitialized(): Boolean = ::exportedLateinit.isInitialized
fun exportedLateinitInitializedByDefault(result: Boolean = ::exportedLateinit.isInitialized): Boolean = result
