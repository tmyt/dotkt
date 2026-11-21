@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "NOTHING_TO_INLINE")

package kotlin

// Allocate by the Kotlin array component. Its physical representation can differ from
// both the ordinary CLR method argument and the storage of a nullable component.
@PublishedApi
@kotlin.clr.ClrArrayFactory("sized")
internal fun <T> dotktNewTypedArray(size: Int): Array<T> = error("CLR array factory")

/** Returns an empty array of the specified type [T]. */
public actual inline fun <reified T> emptyArray(): Array<T> = dotktNewTypedArray<T>(0)
