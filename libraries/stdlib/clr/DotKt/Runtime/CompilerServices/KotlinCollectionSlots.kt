/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

package DotKt.Runtime.CompilerServices

// NOMINAL identities for Kotlin collection classifiers whose operational @ClrTypeAlias faces overlap. They are
// compiler vocabulary: bir2cir attaches the most-specific identity while Kotlin's supertype graph is available.
// The BCL-backed implementations cannot carry these interfaces and are recognized from their real CLR collection
// faces by StarProjectionRuntime instead.
@PublishedApi
internal interface KotlinCollectionClassifier
@PublishedApi
internal interface KotlinSetClassifier : KotlinCollectionClassifier
@PublishedApi
internal interface KotlinMutableSetClassifier : KotlinSetClassifier

// SUPPLEMENTAL KOTLIN SLOTS FOR @ClrTypeAlias'd COLLECTION INTERFACES.
//
// `kotlin.collections.MutableCollection<E>` IS `System.Collections.Generic.ICollection<E>` and
// `MutableList<E>` IS `IList<E>`. Those BCL interfaces carry no slot for Kotlin's mutable `iterator()` return,
// `removeAll`, `retainAll`, `addAll(elements)` or `addAll(index, elements)`, so a call through the Kotlin interface
// has no physical member to dispatch on. Two receiver categories must both work, and they need opposite treatments:
//
//   * a BCL-backed value (`mutableListOf()` -> `List<T>`, `HashSet()`) has no Kotlin body at all and needs a
//     default written over the slots that DO exist (`Remove`/`Add`/`Insert`/`GetEnumerator`);
//   * a Kotlin implementer may OVERRIDE the member, and Kotlin virtual dispatch must reach that override.
//
// These interfaces are the physical carrier of the second category. bir2cir attaches one to every emitted Kotlin
// class that implements such an alias and authors an exact MethodImpl per slot (a private `dotkt$slot$…` bridge
// forwarding to the class's own virtual member), so the override is reachable through a real CLR interface slot.
// The reconciliation itself lives in ONE place, `kotlin.collections.ClrCollectionDefaults`, which tests for these
// interfaces and otherwise runs the BCL default.
//
// WHY THE ELEMENT SURFACE IS ERASED TO `Any`. These interfaces are deliberately NON-generic. Collection arguments
// and the iterator carrier return use `Any` (`System.Object`), so the capability test is independent of the
// instantiation the dispatcher was called at. A generic `…Slots<E>` would instead be correct only as long as a
// separate argument holds:
// that a dispatcher instantiated at `<X>` can only ever receive a receiver whose Kotlin element type is `X`. That is
// true for the collection-mutation dispatchers — they take an INVARIANT `ICollection<T>`/`IList<T>` receiver, so a `<System.Object>`
// instantiation can only be handed an `ICollection<object>` — but it is a property of the helper signatures, not of
// the slot design, and nothing pins it. bir2cir does erase collection element types to `System.Object` elsewhere
// (`clrCollIsEmpty<System.Object>`, `clrCollContainsAll<System.Object>`, `clrCollAdd<object>` all occur in the
// current corpus), so a constructed test would have to be re-argued from scratch after any change to a dispatcher's
// parameter typing, and getting it wrong is fail-OPEN: the override is skipped with no diagnostic. The erased test
// cannot be defeated that way and costs strictly less (one non-generic `isinst`). A collection-argument bridge
// re-establishes the implementer's exact element instantiation. The iterator dispatcher instead adapts the returned
// exact `MutableIterator<E>` at an erased/star call site; it cannot rely on CLR covariance because value-type generic
// arguments do not participate in variance.
//
// NOTE, so the claim is not overstated: no witness of a constructed test actually missing exists, precisely because
// of the invariance argument above. This is a robustness choice, not a reproduced bug fix.
//
// These are compiler vocabulary, not a user-facing API. They are `internal`, which makes them UNNAMEABLE from a
// user module's Kotlin source. They are `@PublishedApi` because bir2cir authors `InterfaceImpl` rows referencing them
// from OTHER assemblies, which requires CLR-public TypeDefs; this is an explicit physical-public contract rather than
// an accidental consequence of interface emission. dll2klib additionally drops the compiler's reserved
// `DotKt.Runtime.CompilerServices` namespace from a projected type's Kotlin supertype list, so a consumer never sees
// one either. `kotlin.collections.ClrCollectionDefaults` is in this same module and resolves them normally.

/** The Kotlin mutable-iterator slot that `System.Collections.Generic.IEnumerable<E>` cannot represent. */
@PublishedApi
internal interface KotlinMutableIteratorSlots {
    /** Returns the implementer's exact `MutableIterator<E>` erased only at this compiler-owned carrier boundary. */
    fun dotktIterator(): Any
}

/** The Kotlin-only `MutableCollection` slots that `System.Collections.Generic.ICollection<E>` does not carry. */
@PublishedApi
internal interface KotlinMutableCollectionSlots {
    /** Physical carrier of `MutableCollection.removeAll`; [elements] is the receiver's own `Collection<E>`. */
    fun dotktRemoveAll(elements: Any): Boolean

    /** Physical carrier of `MutableCollection.retainAll`; [elements] is the receiver's own `Collection<E>`. */
    fun dotktRetainAll(elements: Any): Boolean

    /** Physical carrier of `MutableCollection.addAll`; [elements] is the receiver's own `Collection<E>`. */
    fun dotktAddAll(elements: Any): Boolean
}

/**
 * The Kotlin-only `MutableList` slots that `System.Collections.Generic.IList<E>` does not carry.
 *
 * Deliberately NOT derived from [KotlinMutableCollectionSlots]: the Kotlin frontend materializes inherited interface
 * members as fresh abstract declarations, so a derived interface would restate `dotktRemoveAll`/`dotktRetainAll`/
 * `dotktAddAll` as second declaration slots and an implementer that satisfied only the derived ones would fail to
 * load. A `MutableList` implementer carries BOTH interfaces, each with its own exact MethodImpl rows.
 */
@PublishedApi
internal interface KotlinMutableListSlots {
    /** Physical carrier of `MutableList.addAll(index, elements)`; [elements] is the receiver's own `Collection<E>`. */
    fun dotktAddAllAt(index: Int, elements: Any): Boolean
}
