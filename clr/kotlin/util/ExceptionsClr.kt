@file:Suppress(
    "ACTUAL_WITHOUT_EXPECT",
    "NO_ACTUAL_FOR_EXPECT",
    "UNCHECKED_CAST",
    "NOTHING_TO_INLINE",
    "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS",
    "PLATFORM_CLASS_MAPPED_TO_KOTLIN",
)
// Step-1 CLR stub mirroring JVM actual; bodies are TODO pending @Clr/BCL binding.
// See docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP".

package kotlin

@kotlin.internal.InlineOnly
public actual inline fun Throwable.printStackTrace(): Unit = TODO("clr binding should be implemented")

@SinceKotlin("1.4")
public actual fun Throwable.stackTraceToString(): String = TODO("clr binding should be implemented")

@SinceKotlin("1.1")
@kotlin.internal.HidesMembers
public actual fun Throwable.addSuppressed(exception: Throwable) { TODO("clr binding should be implemented") }

@SinceKotlin("1.4")
public actual val Throwable.suppressedExceptions: List<Throwable>
    get() = TODO("clr binding should be implemented")
