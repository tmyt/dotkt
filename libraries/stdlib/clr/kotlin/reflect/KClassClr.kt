@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.reflect

@kotlin.clr.ClrTypeAlias("System.Object")
@kotlin.clr.ClrValueRepresentation(
    "DotKt.Runtime.CompilerServices.ReflectionRuntimeKt.kClassMatches",
    "DotKt.Runtime.CompilerServices.ReflectionRuntimeKt.kClassClassifier",
    "DotKt.Runtime.CompilerServices.ReflectionRuntimeKt.kClassComponentMatches",
    "DotKt.Runtime.CompilerServices.KClassSlots",
    "",
)
public actual interface KClass<T : Any> : KDeclarationContainer, KAnnotatedElement, KClassifier {
    @get:kotlin.clr.ClrValueMember("DotKt.Runtime.CompilerServices.ReflectionRuntimeKt.kClassSimpleName", "DotKt.Runtime.CompilerServices.KClassSlots.dotktSimpleName")
    public actual val simpleName: String?

    @get:kotlin.clr.ClrValueMember("DotKt.Runtime.CompilerServices.ReflectionRuntimeKt.kClassQualifiedName", "DotKt.Runtime.CompilerServices.KClassSlots.dotktQualifiedName")
    public actual val qualifiedName: String?

    @SinceKotlin("1.1")
    @kotlin.clr.ClrValueMember("DotKt.Runtime.CompilerServices.ReflectionRuntimeKt.kClassIsInstance", "DotKt.Runtime.CompilerServices.KClassSlots.dotktIsInstance")
    public actual fun isInstance(value: Any?): Boolean

    actual override fun equals(other: Any?): Boolean

    actual override fun hashCode(): Int
}
