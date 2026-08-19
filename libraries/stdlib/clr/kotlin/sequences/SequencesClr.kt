/*
 * Copyright 2010-2018 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

// Step-1 CLR stub mirroring the JVM `actual` declarations of this file.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.sequences

// Copied from the JS/Native actual: a plain nullable field consumed once (no AtomicReference).
internal actual class ConstrainedOnceSequence<T> actual constructor(sequence: Sequence<T>) : Sequence<T> {
    private var sequenceRef: Sequence<T>? = sequence

    actual override fun iterator(): Iterator<T> {
        val sequence = sequenceRef ?: throw IllegalStateException("This sequence can be consumed only once.")
        sequenceRef = null
        return sequence.iterator()
    }
}

// The producer has already retained only values satisfying `is T`; this adapter changes the physical sequence element
// interface from object to T without re-running that predicate or eagerly materializing the sequence. The same view
// serves null filtering: its declaration body has already removed nulls before bir2cir replaces the final cast.
internal class ClrSequenceElementAdapter<T>(private val sequence: Sequence<Any?>) : Sequence<T> {
    override fun iterator(): Iterator<T> = ClrSequenceElementIterator(sequence.iterator())
}

private class ClrSequenceElementIterator<T>(private val iterator: Iterator<Any?>) : Iterator<T> {
    @Suppress("UNCHECKED_CAST")
    override fun next(): T = iterator.next() as T

    override fun hasNext(): Boolean = iterator.hasNext()
}
