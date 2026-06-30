// CLR actual mirroring the JS/Native actual: CancellationException gains the (message, cause) and
// (cause) constructors, and the factory functions delegate to them.
// See docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP".
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.coroutines.cancellation

import kotlin.internal.InlineOnly

@SinceKotlin("1.4")
public actual open class CancellationException : IllegalStateException {
    public actual constructor() : super()
    public actual constructor(message: String?) : super(message)
    public constructor(message: String?, cause: Throwable?) : super(message, cause)
    public constructor(cause: Throwable?) : super(cause)
}

// `@Deprecated(level = HIDDEN)` removes the factory from overload resolution so the call below binds to the
// matching constructor (not to itself) — the standard "provided for expect-actual matching" pattern.
@InlineOnly
@SinceKotlin("1.4")
@Deprecated("Provided for expect-actual matching", level = DeprecationLevel.HIDDEN)
public actual inline fun CancellationException(message: String?, cause: Throwable?): CancellationException =
    CancellationException(message, cause)

@InlineOnly
@SinceKotlin("1.4")
@Deprecated("Provided for expect-actual matching", level = DeprecationLevel.HIDDEN)
public actual inline fun CancellationException(cause: Throwable?): CancellationException =
    CancellationException(cause)
