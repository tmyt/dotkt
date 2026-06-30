@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "NOTHING_TO_INLINE")

// The iterator bridge between Kotlin's collection protocol (Iterator.hasNext()/next()) and the BCL's
// (IEnumerator.MoveNext()/Current). Design: docs/design-clr-collection-binding.md. NEVER bind Kotlin Iterator -> CLR
// IEnumerator directly (the semantics differ); bind Iterable -> IEnumerable and bridge here. Proven working (a @Clr List
// iterated via this adapter yields its elements). The reverse direction (a Kotlin Iterable implementor needs a
// generated GetEnumerator) is a separate piece (EnumeratorOverKotlinIterator), not yet wired.

package kotlin.collections

/** The BCL `IEnumerator<T>` surface, for the adapter below. (@ClrTypeAlias -> not emitted; resolves to the real BCL type.) */
@kotlin.clr.ClrTypeAlias("System.Collections.Generic.IEnumerator")
public interface ClrEnumerator<out T> {
    fun MoveNext(): Boolean
    @kotlin.clr.ClrIntrinsic("get_Current") fun current(): T
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
