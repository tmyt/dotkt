/*
 * Copyright 2010-2018 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

package kotlin.sequences

/**
 * A sequence that returns values through its iterator. The values are evaluated lazily, and the sequence
 * is potentially infinite.
 *
 * Sequences can be iterated multiple times, however some sequence implementations might constrain themselves
 * to be iterated only once. That is mentioned specifically in their documentation (e.g. [generateSequence] overload).
 * The latter sequences throw an exception on an attempt to iterate them the second time.
 *
 * Sequence operations, like [Sequence.map], [Sequence.filter] etc, generally preserve that property of a sequence, and
 * again it's documented for an operation if it doesn't.
 *
 * @param T the type of elements in the sequence.
 */
// A Kotlin `Sequence<T>` is a lazily-evaluated pull stream — on the CLR its faithful equivalent IS
// `System.Collections.Generic.IEnumerable<T>` (both yield an element-at-a-time iterator). Bind it as the ONE legit
// stdlib type-alias case so bir2cir resolves a `Sequence<T>` type token (in argTypes/ret/param/local slots emitted by
// birType) to `IEnumerable<T>` uniformly from the ref.dll — retiring the hard-coded netType Sequence branch in kotc.
@kotlin.clr.ClrTypeAlias("System.Collections.Generic.IEnumerable")
public interface Sequence<out T> {
    /**
     * Returns an [Iterator] that returns the values from the sequence.
     *
     * Throws an exception if the sequence is constrained to be iterated once and `iterator` is invoked the second time.
     */
    public operator fun iterator(): Iterator<T>
}
