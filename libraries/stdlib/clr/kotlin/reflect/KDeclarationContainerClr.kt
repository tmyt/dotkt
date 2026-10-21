@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.reflect

@kotlin.clr.ClrTypeAlias("System.Object")
@kotlin.clr.ClrValueRepresentation(
    "DotKt.Runtime.CompilerServices.ReflectionRuntimeKt.declarationContainerMatches",
    "DotKt.Runtime.CompilerServices.ReflectionRuntimeKt.declarationContainerClassifier",
    "DotKt.Runtime.CompilerServices.ReflectionRuntimeKt.declarationContainerComponentMatches",
    "DotKt.Runtime.CompilerServices.KDeclarationContainerSlots",
    "",
)
public interface KDeclarationContainer {
    @kotlin.clr.ClrValueMember("DotKt.Runtime.CompilerServices.ReflectionRuntimeKt.declarationContainerMembers", "DotKt.Runtime.CompilerServices.KDeclarationContainerSlots.dotktMembers")
    public val members: Collection<KCallable<*>>
}
