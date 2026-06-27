// Step-1 CLR stub mirroring the JVM `actual` declarations for this source set.
// Bodies are `TODO` pending the `@Clr`/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").
@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.coroutines

@PublishedApi
@SinceKotlin("1.3")
internal actual class SafeContinuation<in T>
internal actual constructor(
    delegate: Continuation<T>,
    initialResult: Any?
) : Continuation<T> {
    @PublishedApi
    internal actual constructor(delegate: Continuation<T>) : this(delegate, null)

    public actual override val context: CoroutineContext
        get() = TODO("clr binding should be implemented")

    public actual override fun resumeWith(result: Result<T>) { TODO("clr binding should be implemented") }

    @PublishedApi
    internal actual fun getOrThrow(): Any? = TODO("clr binding should be implemented")
}
