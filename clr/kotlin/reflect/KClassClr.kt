@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")
// Step-1 CLR stub mirroring the JVM `actual` declarations of kotlin.reflect.KClass.
// Bodies are `TODO` pending the @Clr/BCL binding step (see docs/design-stdlib-compilation.md "THE CANONICAL ROADMAP").

package kotlin.reflect

public actual interface KClass<T : Any> : KDeclarationContainer, KAnnotatedElement, KClassifier {
    public actual val simpleName: String?

    public actual val qualifiedName: String?

    @SinceKotlin("1.1")
    public actual fun isInstance(value: Any?): Boolean

    actual override fun equals(other: Any?): Boolean

    actual override fun hashCode(): Int
}
