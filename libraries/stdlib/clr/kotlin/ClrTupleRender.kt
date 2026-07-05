/*
 * CLR actual for the Pair/Triple component stringifier (final-review C11). A tuple component's static type is the
 * erased generic parameter, so a nested runtime collection/map reaches the raw .NET `Object.ToString()` and prints
 * `System.Collections.Generic.List`1[System.Int32]` instead of Kotlin's `[1, 2]`. Route through the runtime
 * collection-aware [kotlin.collections.clrElemToString] (the same helper the top-level `println(list)` /
 * `clrCollToString` path uses), which detects an erased collection/map via the non-generic BCL facades and recurses.
 */
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT")

package kotlin

internal actual fun clrRenderTupleElement(value: Any?): String = kotlin.collections.clrElemToString(value)
