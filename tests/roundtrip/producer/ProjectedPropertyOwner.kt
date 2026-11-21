package roundtrip.projectedpropertyowner

interface Source<T> {
    val value: T
    val maybe: T?
}

fun <T> read(source: Source<T>): T = source.value
fun <T> readNullable(source: Source<T>): T? = source.maybe
