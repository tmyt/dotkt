/*
 * Copyright 2010-2018 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// Step-1 CLR stub mirroring the JVM `actual` declarations of this file.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.collections

/**
 * Returns the array if it's not `null`, or an empty array otherwise.
 * @sample samples.collections.Arrays.Usage.arrayOrEmpty
 */
public actual inline fun <reified T> Array<out T>?.orEmpty(): Array<out T> = TODO("clr binding should be implemented")

/**
 * Returns a *typed* array containing all the elements of this collection.
 *
 * Allocates an array of runtime type `T` having its size equal to the size of this collection
 * and populates the array with the elements of this collection.
 * @sample samples.collections.Collections.Collections.collectionToTypedArray
 */
@Suppress("UNCHECKED_CAST")
public actual inline fun <reified T> Collection<T>.toTypedArray(): Array<T> = TODO("clr binding should be implemented")

/** Internal unsafe construction of array based on reference array type */
internal actual fun <T> arrayOfNulls(reference: Array<T>, size: Int): Array<T> = TODO("clr binding should be implemented")
