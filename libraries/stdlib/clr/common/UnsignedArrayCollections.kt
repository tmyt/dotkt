@file:Suppress("NOTHING_TO_INLINE")

package kotlin.collections

// Kotlin/JVM's unsigned-array value classes implement Collection<U>, while DotKt gives them the same native-array
// representation as the signed primitive arrays. Keep the upstream common generated source untouched and supply the
// array-receiver surface from this explicitly CLR-owned common-fragment overlay instead.

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun UIntArray.toMutableList(): MutableList<UInt> =
    ArrayList<UInt>(size).also { list -> for (item in this) list.add(item) }

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun ULongArray.toMutableList(): MutableList<ULong> =
    ArrayList<ULong>(size).also { list -> for (item in this) list.add(item) }

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun UByteArray.toMutableList(): MutableList<UByte> =
    ArrayList<UByte>(size).also { list -> for (item in this) list.add(item) }

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun UShortArray.toMutableList(): MutableList<UShort> =
    ArrayList<UShort>(size).also { list -> for (item in this) list.add(item) }

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun UIntArray.toList(): List<UInt> =
    when (size) { 0 -> emptyList(); 1 -> listOf(this[0]); else -> toMutableList() }

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun ULongArray.toList(): List<ULong> =
    when (size) { 0 -> emptyList(); 1 -> listOf(this[0]); else -> toMutableList() }

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun UByteArray.toList(): List<UByte> =
    when (size) { 0 -> emptyList(); 1 -> listOf(this[0]); else -> toMutableList() }

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun UShortArray.toList(): List<UShort> =
    when (size) { 0 -> emptyList(); 1 -> listOf(this[0]); else -> toMutableList() }

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun UIntArray.isEmpty(): Boolean = size == 0

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun ULongArray.isEmpty(): Boolean = size == 0

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun UByteArray.isEmpty(): Boolean = size == 0

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun UShortArray.isEmpty(): Boolean = size == 0

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun UIntArray.isNotEmpty(): Boolean = size > 0

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun ULongArray.isNotEmpty(): Boolean = size > 0

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun UByteArray.isNotEmpty(): Boolean = size > 0

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public fun UShortArray.isNotEmpty(): Boolean = size > 0

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public operator fun UIntArray.contains(element: UInt): Boolean = indexOf(element) >= 0

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public operator fun ULongArray.contains(element: ULong): Boolean = indexOf(element) >= 0

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public operator fun UByteArray.contains(element: UByte): Boolean = indexOf(element) >= 0

@SinceKotlin("1.3")
@ExperimentalUnsignedTypes
public operator fun UShortArray.contains(element: UShort): Boolean = indexOf(element) >= 0

// Upstream contentToString delegates to joinToString through the JVM Collection supertype. These internal overloads
// preserve that source body for DotKt's native unsigned arrays without adding another public Kotlin API.
@ExperimentalUnsignedTypes
internal fun UIntArray.joinToString(separator: String, prefix: String, postfix: String): String =
    renderUnsignedArray(size, separator, prefix, postfix) { this[it].toString() }

@ExperimentalUnsignedTypes
internal fun ULongArray.joinToString(separator: String, prefix: String, postfix: String): String =
    renderUnsignedArray(size, separator, prefix, postfix) { this[it].toString() }

@ExperimentalUnsignedTypes
internal fun UByteArray.joinToString(separator: String, prefix: String, postfix: String): String =
    renderUnsignedArray(size, separator, prefix, postfix) { this[it].toString() }

@ExperimentalUnsignedTypes
internal fun UShortArray.joinToString(separator: String, prefix: String, postfix: String): String =
    renderUnsignedArray(size, separator, prefix, postfix) { this[it].toString() }

private inline fun renderUnsignedArray(
    size: Int,
    separator: String,
    prefix: String,
    postfix: String,
    element: (Int) -> String,
): String {
    val result = StringBuilder(prefix)
    for (index in 0 until size) {
        if (index != 0) result.append(separator)
        result.append(element(index))
    }
    return result.append(postfix).toString()
}
