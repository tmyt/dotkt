// CLR provision of the Kotlin-compiler intrinsic used by a synthesized data-class / value-class `toString()` when a
// member is an array (e.g. `value class UIntArray(val storage: IntArray)`). On the JVM this lives in
// `kotlin.jvm.internal` and delegates to `java.util.Arrays.toString`; the CLR stdlib was missing it, so the
// synthesized toString referenced it unresolved (`owner=null`) and ilemit aborted with "static method not found".
//
// Minimal emittable body for the stdlib-emit milestone (get the DLL to emit; calling is non-throwing). A faithful
// content-string impl (dispatch each primitive array to `.contentToString()`) can replace this later.
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT")

package kotlin.jvm.internal

public fun dataClassArrayMemberToString(array: Any?): String = array?.toString() ?: "null"

// Sibling intrinsic for a synthesized data-class / value-class `hashCode()` with an array member (JVM: Arrays.hashCode).
public fun dataClassArrayMemberHashCode(array: Any?): Int = array?.hashCode() ?: 0
