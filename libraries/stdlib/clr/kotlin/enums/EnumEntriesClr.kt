@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.enums

// Compiler intrinsic: bir2cir authors the shared cached EnumEntries list operation before generic ABI
// materialization, including during stdlib compilation. This placeholder body is never the implementation
// selected for an intrinsic call; enum values remain semantic until inline specialization has selected T.
@SinceKotlin("1.9")
@PublishedApi
@kotlin.internal.InlineOnly
internal actual inline fun <T : Enum<T>> enumEntriesIntrinsic(): EnumEntries<T> =
    TODO("clr binding should be implemented")
