@file:OptIn(ExperimentalUnsignedTypes::class)

package roundtrip.unsignedarraymembers

fun queryBytes(values: UByteArray, queries: Collection<UByte>): Boolean = values.containsAll(queries)
fun queryShorts(values: UShortArray, queries: Collection<UShort>): Boolean = values.containsAll(queries)
fun queryInts(values: UIntArray, queries: Collection<UInt>): Boolean = values.containsAll(queries)
fun queryLongs(values: ULongArray, queries: Collection<ULong>): Boolean = values.containsAll(queries)
fun savedIntQuery(values: UIntArray): (Collection<UInt>) -> Boolean = values::containsAll
inline fun inlineLongQuery(values: ULongArray, queries: Collection<ULong>): Boolean = values.containsAll(queries)
