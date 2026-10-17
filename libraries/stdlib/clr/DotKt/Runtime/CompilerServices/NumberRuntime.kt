@file:Suppress("DEPRECATION_ERROR")

package DotKt.Runtime.CompilerServices

// An exact native view of the surviving runtime base class. Calls through this view
// retain the real virtual slots; ordinary Number values instead use the adapters below.
@kotlin.clr.ClrTypeAlias("kotlin.Number")
@PublishedApi
internal abstract class RuntimeNumber {
    @kotlin.clr.ClrIntrinsic("toDouble") abstract fun toDouble(): Double
    @kotlin.clr.ClrIntrinsic("toFloat") abstract fun toFloat(): Float
    @kotlin.clr.ClrIntrinsic("toLong") abstract fun toLong(): Long
    @kotlin.clr.ClrIntrinsic("toInt") abstract fun toInt(): Int
    @kotlin.clr.ClrIntrinsic("toShort") abstract fun toShort(): Short
    @kotlin.clr.ClrIntrinsic("toByte") abstract fun toByte(): Byte
    @kotlin.clr.ClrIntrinsic("toChar") abstract fun toChar(): Char
}

@PublishedApi
internal fun numberMatches(value: Any?, arrayDepth: Int): Boolean {
    if (arrayDepth == 0) return value is Byte || value is Short || value is Int || value is Long ||
        value is Float || value is Double || value is RuntimeNumber
    return value != null && numberArrayTypeMatches(value.starProjectionRuntimeType(), arrayDepth)
}

private fun numberArrayTypeMatches(type: StarProjectionType, depth: Int): Boolean {
    if (!type.isSzArray) return false
    val component = type.getElementType()!!
    // Erased allocations retain the existing object[] component contract. A more
    // precise native component must still supply the requested Kotlin classifier.
    if (depth > 1) return numberArrayTypeMatches(component, depth - 1)
    return component == (Any::class as StarProjectionType) || component == (Byte::class as StarProjectionType) ||
        component == (Short::class as StarProjectionType) || component == (Int::class as StarProjectionType) ||
        component == (Long::class as StarProjectionType) || component == (Float::class as StarProjectionType) ||
        component == (Double::class as StarProjectionType) ||
        (RuntimeNumber::class as StarProjectionType).isAssignableFrom(component)
}

@PublishedApi
internal fun numberClassifier(arrayDepth: Int): (Any?) -> Boolean = { numberMatches(it, arrayDepth) }

@PublishedApi
internal fun numberToDouble(value: Any): Double = when (value) {
    is Byte -> value.toDouble()
    is Short -> value.toDouble()
    is Int -> value.toDouble()
    is Long -> value.toDouble()
    is Float -> value.toDouble()
    is Double -> value
    else -> (value as RuntimeNumber).toDouble()
}

@PublishedApi
internal fun numberToFloat(value: Any): Float = when (value) {
    is Byte -> value.toFloat()
    is Short -> value.toFloat()
    is Int -> value.toFloat()
    is Long -> value.toFloat()
    is Float -> value
    is Double -> value.toFloat()
    else -> (value as RuntimeNumber).toFloat()
}

@PublishedApi
internal fun numberToLong(value: Any): Long = when (value) {
    is Byte -> value.toLong()
    is Short -> value.toLong()
    is Int -> value.toLong()
    is Long -> value
    is Float -> value.toLong()
    is Double -> value.toLong()
    else -> (value as RuntimeNumber).toLong()
}

@PublishedApi
internal fun numberToInt(value: Any): Int = when (value) {
    is Byte -> value.toInt()
    is Short -> value.toInt()
    is Int -> value
    is Long -> value.toInt()
    is Float -> value.toInt()
    is Double -> value.toInt()
    else -> (value as RuntimeNumber).toInt()
}

@PublishedApi
internal fun numberToShort(value: Any): Short = when (value) {
    is Byte -> value.toShort()
    is Short -> value
    is Int -> value.toShort()
    is Long -> value.toShort()
    is Float -> value.toInt().toShort()
    is Double -> value.toInt().toShort()
    else -> (value as RuntimeNumber).toShort()
}

@PublishedApi
internal fun numberToByte(value: Any): Byte = when (value) {
    is Byte -> value
    is Short -> value.toByte()
    is Int -> value.toByte()
    is Long -> value.toByte()
    is Float -> value.toInt().toByte()
    is Double -> value.toInt().toByte()
    else -> (value as RuntimeNumber).toByte()
}

@PublishedApi
internal fun numberToChar(value: Any): Char =
    if (value is RuntimeNumber) value.toChar() else numberToInt(value).toChar()
