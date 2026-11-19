@file:Suppress("ACTUAL_WITHOUT_EXPECT", "NO_ACTUAL_FOR_EXPECT", "UNCHECKED_CAST", "NOTHING_TO_INLINE", "NO_ACTUAL_CLASS_MEMBER_FOR_EXPECTED_CLASS")

package kotlin.reflect

public actual interface KType : KAnnotatedElement {
    @SinceKotlin("1.1")
    public actual val classifier: KClassifier?

    @SinceKotlin("1.1")
    public actual val arguments: List<KTypeProjection>

    public actual val isMarkedNullable: Boolean
}

// The classifier's Kotlin name is independent of its CLR storage (e.g.
// kotlin.String vs System.String). Only the source type supplies the arguments.
internal data class ClrReflectedType(
    private val classifierName: String,
    override val classifier: KClassifier?,
    override val arguments: List<KTypeProjection>,
    override val isMarkedNullable: Boolean,
) : KType {
    override val annotations: List<Annotation> get() = emptyList()

    override fun toString(): String = buildString {
        append(classifierName)
        if (arguments.isNotEmpty()) {
            append('<')
            for (index in arguments.indices) {
                if (index != 0) append(", ")
                append(arguments[index])
            }
            append('>')
        }
        if (isMarkedNullable) append('?')
    }
}

// Unlike a reified argument, a parameter in a declaration denotes the same
// classifier at every invocation. Overloads can each declare a parameter "T".
internal class ClrDeclaredParameter(
    private val module: Any,
    private val declaration: String,
    private val index: Int,
    override val name: String,
    override val variance: KVariance,
    override val isReified: Boolean,
    override val upperBounds: List<KType>,
) : KTypeParameter {
    override fun equals(other: Any?): Boolean = other is ClrDeclaredParameter &&
        module == other.module && declaration == other.declaration && index == other.index
    override fun hashCode(): Int = (31 * module.hashCode() + declaration.hashCode()) * 31 + index
    override fun toString(): String = name
}
