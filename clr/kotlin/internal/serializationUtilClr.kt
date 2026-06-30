@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

// Step-1 CLR stub mirroring the JVM actual; bodies are TODO pending the @Clr/BCL binding step
// (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.internal

@InlineOnly
internal actual inline fun throwReadObjectNotSupported(): Nothing =
    throw UnsupportedOperationException("Deserialization is not supported")

// No Java-style serialization on the CLR: nothing to wrap, just run the action.
@InlineOnly
internal actual inline fun wrapAsDeserializationException(action: () -> Unit) { action() }

internal actual class ReadObjectParameterType
