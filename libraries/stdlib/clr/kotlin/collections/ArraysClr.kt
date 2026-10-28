/*
 * Copyright 2010-2018 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.collections

/**
 * Returns the array if it's not `null`, or an empty array otherwise.
 * @sample samples.collections.Arrays.Usage.arrayOrEmpty
 */
public actual inline fun <reified T> Array<out T>?.orEmpty(): Array<out T> = this ?: emptyArray<T>()

/**
 * Returns a *typed* array containing all the elements of this collection.
 *
 * Allocates an array of runtime type `T` having its size equal to the size of this collection
 * and populates the array with the elements of this collection.
 * @sample samples.collections.Collections.Collections.collectionToTypedArray
 */
@Suppress("UNCHECKED_CAST")
public actual inline fun <reified T> Collection<T>.toTypedArray(): Array<T> {
    val result = dotktNewTypedArray(T::class as DotktType, size) as Array<T>
    val iterator = iterator()
    var index = 0
    while (iterator.hasNext()) {
        result[index++] = iterator.next()
    }
    return result
}

/** Internal unsafe construction of array based on reference array type */
// Preserve the supplied array's actual CLR component, including value types,
// when the shared collection helper needs a larger destination.
@kotlin.clr.ClrIntrinsic("GetType")                                     // object.GetType() -> the receiver's runtime array Type
private fun Any.arrClrGetType(): Any = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Array.CreateInstanceFromArrayType")    // (Type arrayType, int length) -> Array; receiver(arrayType) -> arg0
private fun Any.arrClrCreateArrayLike(length: Int): Any = TODO("clr binding should be implemented")

internal actual fun <T> arrayOfNulls(reference: Array<T>, size: Int): Array<T> =
    (reference as Any).arrClrGetType().arrClrCreateArrayLike(size) as Array<T>
