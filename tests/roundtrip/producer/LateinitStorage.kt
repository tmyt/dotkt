package roundtrip.lateinitstorage

class LateinitBox<T : Any> {
    lateinit var value: T
    fun initialized(): Boolean = this::value.isInitialized
    fun other(other: LateinitBox<T>): Boolean = other::value.isInitialized
    fun reference(): kotlin.reflect.KMutableProperty0<T> = this::value
}

lateinit var exportedLateinit: String
fun exportedLateinitInitialized(): Boolean = ::exportedLateinit.isInitialized
