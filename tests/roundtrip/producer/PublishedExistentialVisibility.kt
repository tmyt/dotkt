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
