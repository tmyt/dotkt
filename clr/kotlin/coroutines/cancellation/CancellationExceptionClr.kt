// Step-1 CLR stub mirroring the JVM `actual` declarations for this source set.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.coroutines.cancellation

import kotlin.internal.InlineOnly

@SinceKotlin("1.4")
public actual open class CancellationException : IllegalStateException {
    public actual constructor()
    public actual constructor(message: String?)
}

@InlineOnly
@SinceKotlin("1.4")
public actual inline fun CancellationException(message: String?, cause: Throwable?): CancellationException = TODO("clr binding should be implemented")

@InlineOnly
@SinceKotlin("1.4")
public actual inline fun CancellationException(cause: Throwable?): CancellationException = TODO("clr binding should be implemented")
