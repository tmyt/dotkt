// #43: a cross-module inline payload whose body creates a non-capturing lambda.
// The lambda is lifted to an origin-file __lambdaN and represented by newDelegate in the
// [KotlinInline] payload. The consumer must be able to materialize that value without
// referring to a private implementation detail that exists only in the producer file.
package roundtrip.inldelegate

inline fun applyViaNestedDelegate(value: Int, block: (Int) -> Int): Int {
    val twice: (Int) -> Int = { it * 2 }
    return block(twice(value))
}

inline fun <T> applyViaGenericNestedDelegate(value: T, block: (T) -> T): T {
    val identity: (T) -> T = { it }
    return block(identity(value))
}

inline fun <T> applyViaNullableResult(value: T, block: (List<T?>) -> T?): T? {
    val produce: (T) -> List<T?> = { listOf(it) }
    return block(produce(value))
}

inline fun <T> applyViaNullableArgument(values: List<T?>, block: (T?) -> T?): T? {
    val consume: (List<T?>) -> T? = { it.firstOrNull() }
    return block(consume(values))
}

fun <T> nullableSingleton(value: T): List<T?> = listOf(value)

inline fun <T> applyViaNullableReference(value: T, block: (List<T?>) -> T?): T? {
    val produce: (T) -> List<T?> = ::nullableSingleton
    return block(produce(value))
}

inline fun applyViaTransitivelyNestedDelegate(value: Int, block: (Int) -> Int): Int {
    val outer: (Int) -> Int = { n ->
        val inner: (Int) -> Int = { it * 3 }
        inner(n)
    }
    return block(outer(value))
}

class NestedDelegateHost {
    inline fun apply(value: Int, block: (Int) -> Int): Int {
        val increment: (Int) -> Int = { it + 1 }
        return block(increment(value))
    }
}
