/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM `actual`; bodies are `TODO` pending the `@Clr`/BCL
// binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin

/**
 * Classes which inherit from this interface have a defined total ordering between their instances.
 */
// Type-alias to the BCL System.IComparable<in T> (a TYPE binding, like List->IReadOnlyList): a CLR primitive
// (System.Int32) implements IComparable<int> but NOT kotlin.Comparable, so `sorted`'s `toTypedArray<Comparable<T>>`
// type-arg must resolve to IComparable<T> for the element cast to succeed. compareTo -> IComparable.CompareTo (consistent
// with the bound-drop + the constrained-call compareTo lowering, which already use System.IComparable).
@kotlin.clr.ClrIntrinsic("System.IComparable")
public actual interface Comparable<in T> {
    /**
     * Compares this object with the specified object for order. Returns zero if this object is equal
     * to the specified [other] object, a negative number if it's less than [other], or a positive number
     * if it's greater than [other].
     */
    @kotlin.clr.ClrIntrinsic("CompareTo")
    public actual operator fun compareTo(other: T): Int
}
