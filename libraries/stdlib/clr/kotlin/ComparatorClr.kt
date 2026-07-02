@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin

// Plain Kotlin fun interface. (An earlier @ClrIntrinsic("System.Collections.IComparer") alias was a WORKAROUND for a
// misdiagnosed value-type bug; the real bug was `x as T` emitting `castclass !!T` instead of `unbox.any !!T` in ilemit
// -- fixed at the source, so the IComparer erasure is unnecessary. A SAM `Comparator { a, b -> ... }` lowers to a
// synthetic class implementing this interface, NOT a Func delegate -- see samConversion.)
public actual fun interface Comparator<T> {
    public actual fun compare(a: T, b: T): Int
}
