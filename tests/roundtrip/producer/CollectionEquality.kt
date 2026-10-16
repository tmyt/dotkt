package roundtrip.collectionequality

fun <T> equalInProducer(left: T, right: T): Boolean = left == right
fun <T> explicitInProducer(left: T, right: T): Boolean = left!!.equals(right)
inline fun <T> inlineEqual(left: T, right: T): Boolean = left == right
fun producerList(): Any = listOf(23, 42)
fun producerSet(): Any = setOf(23, 42)
fun producerMap(): Any = mapOf(23 to "x", 42 to "y")

class EqualityStorage(val value: Any?) {
    fun sameAs(other: Any?): Boolean = value == other
}

data class GenericEqualityData<T>(val value: T, val tag: String)

open class DefaultList : List<Int> by listOf(23, 42)

class SuperDefaultList : DefaultList() {
    override fun equals(other: Any?): Boolean = super.equals(other)
}

class CountingList : DefaultList() {
    var calls = 0
    override fun equals(other: Any?): Boolean {
        calls++
        return other is List<*> && other.size == 2 && other[0] == 23 && other[1] == 42
    }
    override fun hashCode(): Int = 1716
}

class ObservedEquality {
    var calls = 0
    override fun equals(other: Any?): Boolean { calls++; return false }
    override fun hashCode(): Int = 0
}

class OverloadedEquality {
    fun equals(other: String): Boolean = other == "x"
}
