@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin

@SinceKotlin("2.0")
public actual interface AutoCloseable {
    public actual fun close(): Unit
}

@SinceKotlin("2.0")
@kotlin.internal.InlineOnly
public actual inline fun AutoCloseable(crossinline closeAction: () -> Unit): AutoCloseable =
    TODO("clr binding should be implemented")

@SinceKotlin("1.2")
@kotlin.internal.InlineOnly
public actual inline fun <T : AutoCloseable?, R> T.use(block: (T) -> R): R {
    TODO("clr binding should be implemented")
}
