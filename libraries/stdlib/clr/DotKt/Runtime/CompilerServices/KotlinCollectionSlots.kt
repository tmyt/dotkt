/*
 * Copyright 2010-2024 JetBrains s.r.o. and Kotlin Programming Language contributors.
 * Use of this source code is governed by the Apache 2.0 license that can be found in the license/LICENSE.txt file.
 */

package DotKt.Runtime.CompilerServices

// SUPPLEMENTAL KOTLIN SLOTS FOR @ClrTypeAlias'd COLLECTION INTERFACES.
//
// `kotlin.collections.MutableCollection<E>` IS `System.Collections.Generic.ICollection<E>` and
// `MutableList<E>` IS `IList<E>`. Those BCL interfaces carry no slot for Kotlin's `removeAll`, `retainAll`,
// `addAll(elements)` or `addAll(index, elements)`, so a call through the Kotlin interface has no physical member
// to dispatch on. Two receiver categories must both work, and they need opposite treatments:
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
// WHY THE ELEMENT SURFACE IS ERASED TO `Any`. These interfaces are deliberately NON-generic and their element
// collection parameter is `Any` (`System.Object`). A generic `…Slots<E>` would make the capability test depend on
// the CALL SITE's generic fidelity: bir2cir legitimately erases a collection element type to `System.Object` in
// several places (measured on the current corpus: `clrCollIsEmpty<System.Object>` in `Collections__3`/`Sets`,
// `clrCollContainsAll<System.Object>` in `AbstractSet`, `clrCollAdd<object>` in `NullableTests`/`Gencoll`), and at
// such a site `receiver is …Slots<object>` would MISS a `Counting<int>` receiver and silently fall through to the
// BCL default — a fail-OPEN path that skips a user override with no diagnostic. An erased, instantiation-independent
// test cannot miss. The bridge re-establishes the exact type by casting back to the implementer's own element
// instantiation, so a genuinely mismatched argument fails LOUD (InvalidCastException) instead of silently.
//
// These are compiler vocabulary, not a user-facing API: user code must not implement them directly (the compiler
// authors every implementation). They are public because bir2cir emits `InterfaceImpl` rows referencing them from
// OTHER assemblies, which requires CLR-public accessibility.

/** The Kotlin-only `MutableCollection` slots that `System.Collections.Generic.ICollection<E>` does not carry. */
public interface KotlinMutableCollectionSlots {
    /** Physical carrier of `MutableCollection.removeAll`; [elements] is the receiver's own `Collection<E>`. */
    public fun dotktRemoveAll(elements: Any): Boolean

    /** Physical carrier of `MutableCollection.retainAll`; [elements] is the receiver's own `Collection<E>`. */
    public fun dotktRetainAll(elements: Any): Boolean

    /** Physical carrier of `MutableCollection.addAll`; [elements] is the receiver's own `Collection<E>`. */
    public fun dotktAddAll(elements: Any): Boolean
}

/**
 * The Kotlin-only `MutableList` slots that `System.Collections.Generic.IList<E>` does not carry.
 *
 * Deliberately NOT derived from [KotlinMutableCollectionSlots]: the Kotlin frontend materializes inherited interface
 * members as fresh abstract declarations, so a derived interface would restate `dotktRemoveAll`/`dotktRetainAll`/
 * `dotktAddAll` as second declaration slots and an implementer that satisfied only the derived ones would fail to
 * load. A `MutableList` implementer carries BOTH interfaces, each with its own exact MethodImpl rows.
 */
public interface KotlinMutableListSlots {
    /** Physical carrier of `MutableList.addAll(index, elements)`; [elements] is the receiver's own `Collection<E>`. */
    public fun dotktAddAllAt(index: Int, elements: Any): Boolean
}
