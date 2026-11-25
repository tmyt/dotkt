package metadata

internal class WithProperty(val size: Int)

internal fun readProperty(value: WithProperty): Int {
    val get: (WithProperty) -> Int = WithProperty::size
    return get(value)
}
