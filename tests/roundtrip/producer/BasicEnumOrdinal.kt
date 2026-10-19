package roundtrip.basicenum

import roundtrip.entries.BasicEntry

class BasicEnumHolder {
    enum class Nested { FIRST, SECOND, LAST }
}

fun producerBasicOrdinal(value: BasicEntry): Int = value.ordinal
fun <E : Enum<E>> producerGenericOrdinal(value: E): Int = value.ordinal
