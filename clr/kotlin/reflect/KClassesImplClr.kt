@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")
// Step-1 CLR stub mirroring the JVM `actual` declarations of kotlin.reflect.KClassesImpl.
// Bodies are `TODO` pending the @Clr/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.reflect

internal actual inline val KClass<*>.qualifiedOrSimpleName: String?
    get() = TODO("clr binding should be implemented")
