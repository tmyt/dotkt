@file:Suppress("DEPRECATION_ERROR")

package DotKt.Runtime.CompilerServices

// Kotlin implementations carry real nominal slots without claiming that native numeric
// boxes inherit a runtime base class. This compiler-owned face is not semantic Number.
@PublishedApi
internal interface NumberSlots {
    fun dotktToDouble(): Double
    fun dotktToFloat(): Float
    fun dotktToLong(): Long
    fun dotktToInt(): Int
    fun dotktToShort(): Short
    fun dotktToByte(): Byte
    fun dotktToChar(): Char
}

@kotlin.clr.ClrTypeAlias("System.Array")
private interface NumberArrayStorage

@PublishedApi
internal fun numberMatches(value: Any?, arrayShape: String): Boolean {
    if (arrayShape.length == 0) return value is Byte || value is Short || value is Int || value is Long ||
        value is Float || value is Double || value is NumberSlots
    return value != null && numberArrayTypeMatches(value.starProjectionRuntimeType(), arrayShape, 0)
}

private fun numberArrayTypeMatches(type: StarProjectionType, shape: String, index: Int): Boolean {
    if (!type.isSzArray) return false
    val component = type.getElementType()!!
    // Erased allocations retain the existing object[] component contract. A more
    // precise native component must still supply the requested Kotlin classifier.
    if (index + 1 < shape.length) {
        // Nested projected/open arrays allocate System.Array[]; the erased component
        // already supplies that level's storage, just as in ordinary array casts.
        if (shape[index + 1] == 'o' && component == (NumberArrayStorage::class as StarProjectionType)) return true
        return numberArrayTypeMatches(component, shape, index + 1)
    }
    return component == (Any::class as StarProjectionType) || numberComponentMatches(component)
}

@PublishedApi
internal fun numberComponentMatches(component: StarProjectionType): Boolean =
    component == (Byte::class as StarProjectionType) ||
        component == (Short::class as StarProjectionType) || component == (Int::class as StarProjectionType) ||
        component == (Long::class as StarProjectionType) || component == (Float::class as StarProjectionType) ||
        component == (Double::class as StarProjectionType) ||
        (NumberSlots::class as StarProjectionType).isAssignableFrom(component)

@PublishedApi
internal fun numberClassifier(arrayShape: String): (Any?) -> Boolean = { numberMatches(it, arrayShape) }

@PublishedApi
internal fun numberToDouble(value: Any): Double = when (value) {
    is Byte -> value.toDouble()
    is Short -> value.toDouble()
    is Int -> value.toDouble()
    is Long -> value.toDouble()
    is Float -> value.toDouble()
    is Double -> value
    else -> (value as NumberSlots).dotktToDouble()
}

@PublishedApi
internal fun numberToFloat(value: Any): Float = when (value) {
    is Byte -> value.toFloat()
    is Short -> value.toFloat()
    is Int -> value.toFloat()
    is Long -> value.toFloat()
    is Float -> value
    is Double -> value.toFloat()
    else -> (value as NumberSlots).dotktToFloat()
}

@PublishedApi
internal fun numberToLong(value: Any): Long = when (value) {
    is Byte -> value.toLong()
    is Short -> value.toLong()
    is Int -> value.toLong()
    is Long -> value
    is Float -> value.toLong()
    is Double -> value.toLong()
    else -> (value as NumberSlots).dotktToLong()
}

@PublishedApi
internal fun numberToInt(value: Any): Int = when (value) {
    is Byte -> value.toInt()
    is Short -> value.toInt()
    is Int -> value
    is Long -> value.toInt()
    is Float -> value.toInt()
    is Double -> value.toInt()
    else -> (value as NumberSlots).dotktToInt()
}

@PublishedApi
internal fun numberToShort(value: Any): Short = when (value) {
    is Byte -> value.toShort()
    is Short -> value
    is Int -> value.toShort()
    is Long -> value.toShort()
    is Float -> value.toInt().toShort()
    is Double -> value.toInt().toShort()
    else -> (value as NumberSlots).dotktToShort()
}

@PublishedApi
internal fun numberToByte(value: Any): Byte = when (value) {
    is Byte -> value
    is Short -> value.toByte()
    is Int -> value.toByte()
    is Long -> value.toByte()
    is Float -> value.toInt().toByte()
    is Double -> value.toInt().toByte()
    else -> (value as NumberSlots).dotktToByte()
}

@PublishedApi
internal fun numberToChar(value: Any): Char =
    if (value is NumberSlots) value.dotktToChar() else numberToInt(value).toChar()
