@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin

// Allocate using the CLR storage argument. A Kotlin classifier identifies a declaration
// across all of its generic instantiations and is not an array element storage token.
@PublishedApi
@kotlin.clr.ClrIntrinsic("System.GC.AllocateArray")
internal fun <T> dotktNewTypedArray(length: Int, pinned: Boolean): Array<T> = error("CLR binding")

/**
 * Returns an empty array of the specified type [T].
 */
public actual inline fun <reified T> emptyArray(): Array<T> = dotktNewTypedArray<T>(0, false)
