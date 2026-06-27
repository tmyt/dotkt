@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.internal

@InlineOnly
internal actual inline fun throwReadObjectNotSupported(): Nothing =
    TODO("clr binding should be implemented")

@InlineOnly
internal actual inline fun wrapAsDeserializationException(action: () -> Unit) { TODO("clr binding should be implemented") }

internal actual class ReadObjectParameterType
