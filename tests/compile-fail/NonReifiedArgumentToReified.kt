private inline fun <reified T> acceptsReified(value: Any?): Boolean = value is T

public fun <U> rejectsOrdinaryForward(value: Any?): Boolean = acceptsReified<U>(value)
