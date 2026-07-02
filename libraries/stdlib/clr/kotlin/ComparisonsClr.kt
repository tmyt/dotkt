/*
 * CLR raw-comparable binding for kotlin.comparisons.
 */

package kotlin.comparisons

/**
 * The NON-generic `System.IComparable` view of a value. `compareValues` cannot keep the JVM's
 * `a as Comparable<Any>` unchecked cast: under the reified alias it lowers to `IComparable<object>`,
 * which a boxed CLR primitive does NOT implement (`Int32 : IComparable<int>` only) →
 * InvalidCastException. Every BCL comparable (primitives, String, DateTime, enums, …) DOES implement
 * the non-generic `System.IComparable`, whose `CompareTo` takes `object` — the CLR-faithful
 * erased-comparable dispatch.
 */
@kotlin.clr.ClrTypeAlias("System.IComparable")
internal interface ClrRawComparable {
    @kotlin.clr.ClrIntrinsic("CompareTo")
    fun compareTo(other: Any?): Int
}

internal actual fun clrRawCompareTo(a: Any, b: Any): Int = (a as ClrRawComparable).compareTo(b)
