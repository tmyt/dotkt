/*
 * Copyright 2010-2018 JetBrains s.r.o. Use of this source code is governed by the Apache 2.0 license
 * that can be found in the LICENSE file.
 */

package kotlin.collections

/**
 * Returns a hash code based on the contents of this array as if it is [List].
 * Nested arrays are treated as lists too.
 *
 * If any of arrays contains itself on any nesting level the behavior is undefined.
 */
@PublishedApi
@SinceKotlin("1.1")
@OptIn(ExperimentalUnsignedTypes::class)
internal fun <T> Array<out T>?.contentDeepHashCodeImpl(): Int {
    if (this == null) return 0
    var result = 1
    for (element in this) {
        val elementHash = when (element) {
            null            -> 0

            is Array<*>     -> element.contentDeepHashCode()

            is ByteArray    -> element.contentHashCode()
            is ShortArray   -> element.contentHashCode()
            is IntArray     -> element.contentHashCode()
            is LongArray    -> element.contentHashCode()
            is FloatArray   -> element.contentHashCode()
            is DoubleArray  -> element.contentHashCode()
            is CharArray    -> element.contentHashCode()
            is BooleanArray -> element.contentHashCode()

            is UByteArray   -> element.contentHashCode()
            is UShortArray  -> element.contentHashCode()
            is UIntArray    -> element.contentHashCode()
            is ULongArray   -> element.contentHashCode()

            else            -> element.hashCode()
        }

        result = 31 * result + elementHash
    }
    return result
}
