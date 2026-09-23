package roundtrip.inlineclosureownership

fun interface ClosureSupplier<T> { fun read(): T }

open class ClosureOwner<T>(val value: T) {
    fun sameOwner(): T = invokeBlock { value }

    inline fun invokeBlock(crossinline block: () -> T): T {
        val invoke = { block() }
        return invoke()
    }

    inline fun deferred(crossinline block: () -> T): () -> T = { block() }

    inline fun nested(crossinline block: () -> T): () -> () -> T = { { block() } }

    inline fun supplier(crossinline block: () -> T): ClosureSupplier<T> = ClosureSupplier { block() }

    fun fromDefault(block: () -> T = { value }): T = block()
}
