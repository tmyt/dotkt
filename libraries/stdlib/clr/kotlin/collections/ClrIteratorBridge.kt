@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "NOTHING_TO_INLINE")

// The iterator bridge between Kotlin's collection protocol (Iterator.hasNext()/next()) and the BCL's
// (IEnumerator.MoveNext()/Current). Design: docs/design-clr-collection-binding.md. NEVER bind Kotlin Iterator -> CLR
// IEnumerator directly (the semantics differ); bind Iterable -> IEnumerable and bridge here. Proven working (a @Clr List
// iterated via this adapter yields its elements). The REVERSE direction — a Kotlin Iterable implementor owing the CLR a
// GetEnumerator — cannot be written in Kotlin at all (IEnumerator<T> and the non-generic IEnumerator declare two
// `Current` slots differing only in return type), so bir2cir authors it as CIR: the compiler-owned
// `dotkt$EnumeratorOverKotlinIterator<T>` adapter plus both GetEnumerator halves on each implementer.

package kotlin.collections

/** The BCL `IEnumerator<T>` surface, for the adapter below. (@ClrTypeAlias -> not emitted; resolves to the real BCL type.) */
@kotlin.clr.ClrTypeAlias("System.Collections.Generic.IEnumerator")
public interface ClrEnumerator<out T> {
    fun MoveNext(): Boolean
    @kotlin.clr.ClrProperty(kotlin.clr.READ, "Current") fun current(): T
}

/** The BCL `IEnumerable<T>` surface — its `GetEnumerator()` returns the INTERFACE `IEnumerator<T>` (a `List<T>` value's
 *  own `GetEnumerator` returns the struct `List<T>.Enumerator` instead, so always go through this interface). */
@kotlin.clr.ClrTypeAlias("System.Collections.Generic.IEnumerable")
public interface ClrEnumerable<out T> {
    fun GetEnumerator(): ClrEnumerator<T>
}

/** Kotlin `Iterator<T>` (hasNext/next) over a BCL `IEnumerator<T>` (MoveNext/Current). `hasNext()` buffers by calling
 *  `MoveNext()` at most once per element; `next()` consumes the buffered state and returns `Current`. */
internal class KotlinIteratorOverEnumerator<out T>(private val e: ClrEnumerator<T>) : Iterator<T> {
    private var state: Int = 0   // 0 = unknown, 1 = has current buffered, 2 = done
    override fun hasNext(): Boolean {
        if (state == 1) return true
        if (state == 2) return false
        return if (e.MoveNext()) { state = 1; true } else { state = 2; false }
    }
    override fun next(): T {
        if (!hasNext()) throw NoSuchElementException()
        state = 0
        return e.current()
    }
}

/** Wrap a BCL enumerable as a Kotlin `Iterator<T>`. `Iterable<T>.iterator()` (CLR-bound to IEnumerable) delegates here;
 *  because the default body is Kotlin, rule 3 hoists it to a static helper automatically. */
public fun <T> iteratorOverEnumerable(self: ClrEnumerable<T>): Iterator<T> =
    KotlinIteratorOverEnumerator(self.GetEnumerator())

/** The RAW (non-generic `System.Collections.IEnumerator`) twin of [KotlinIteratorOverEnumerator] — #74b(ii): a
 *  star-projected/erased collection (`Collection<*>`/`Map<*,*>`) only implements the NON-generic BCL enumerable
 *  facade (`ClrRawEnumerable`/`ClrRawEnumerator`, ClrNestedToString.kt), never a reified `IEnumerable<object>` —
 *  so StarProjectionLowering's `.iterator()` binding must wrap THIS enumerator, not the generic one above, while
 *  still producing a genuine `Iterator<Any?>` (so the ordinary hasNext/next consumer dispatch — which re-points
 *  at the REAL referenced `kotlin.collections.Iterator<E>` — resolves against an object that actually implements it). */
internal class KotlinIteratorOverRawEnumerator(private val e: ClrRawEnumerator) : Iterator<Any?> {
    private var state: Int = 0   // 0 = unknown, 1 = has current buffered, 2 = done
    override fun hasNext(): Boolean {
        if (state == 1) return true
        if (state == 2) return false
        return if (e.MoveNext()) { state = 1; true } else { state = 2; false }
    }
    override fun next(): Any? {
        if (!hasNext()) throw NoSuchElementException()
        state = 0
        return e.current()
    }
}

/** Wrap a RAW (non-generic) BCL enumerable as a Kotlin `Iterator<Any?>` — the star-projected/erased-collection
 *  `.iterator()` target (#74b(ii)). Every substituted collection implements the non-generic `System.Collections
 *  .IEnumerable` facade regardless of its erased element type; `self` is typed `Any` (not the `internal
 *  ClrRawEnumerable` alias directly — a PUBLIC function may not expose an internal parameter type, the same
 *  reason `clrElemToString`/`clrMapGet` cast internally instead of taking the raw facade as their param). */
public fun iteratorOverRawEnumerable(self: Any): Iterator<Any?> =
    KotlinIteratorOverRawEnumerator((self as ClrRawEnumerable).GetEnumerator())
