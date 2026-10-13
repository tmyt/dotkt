package roundtrip.publishedexistential

@PublishedApi
internal open class PublishedCell<T>(val value: T) {
    fun label(): String = "published-class"
}

@PublishedApi
internal interface PublishedContract<T> {
    fun label(): String
}

@PublishedApi
internal class PublishedImplementation<T>(val value: T) : PublishedContract<T> {
    override fun label(): String = "published-interface"
}

@PublishedApi
internal object PublishedObject {
    fun label(): String = "published-object"
}

class PublicPublishedVisibilityControl {
    @PublishedApi
    internal class Nested {
        fun label(): String = "published-nested"
    }
}

@PublishedApi
internal enum class PublishedEnum { First, Second }

inline fun publishedNonGeneric(): String = PublishedObject.label() +
    ":" + PublicPublishedVisibilityControl.Nested().label() + ":" + PublishedEnum.Second.ordinal

inline fun <T> publishedClass(value: T, block: (T) -> Unit): String {
    val cell = PublishedCell(value)
    block(cell.value)
    val erased: PublishedCell<*> = cell
    return erased.label()
}

inline fun <T> publishedInterface(value: T, block: (T) -> Unit): String {
    val implementation = PublishedImplementation(value)
    block(implementation.value)
    val erased: PublishedContract<*> = implementation
    return erased.label()
}
