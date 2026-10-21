package DotKt.Runtime.CompilerServices

import kotlin.reflect.KCallable

@PublishedApi
internal interface KotlinClassifierMarker

@PublishedApi
internal interface KAnnotatedElementSlots {
    fun dotktAnnotations(): List<Annotation>
}

@PublishedApi
internal interface KDeclarationContainerSlots {
    fun dotktMembers(): Collection<KCallable<*>>
}

@PublishedApi
internal interface KClassSlots : KotlinClassifierMarker, KAnnotatedElementSlots, KDeclarationContainerSlots {
    fun dotktSimpleName(): String?
    fun dotktQualifiedName(): String?
    fun dotktIsInstance(value: Any?): Boolean
}

@kotlin.clr.ClrTypeAlias("System.Type")
private interface NativeReflectionType {
    @get:kotlin.clr.ClrProperty(kotlin.clr.READ, "Name") val name: String
    @get:kotlin.clr.ClrProperty(kotlin.clr.READ, "FullName") val fullName: String?
    @kotlin.clr.ClrIntrinsic("IsInstanceOfType") fun isInstance(value: Any?): Boolean
    @kotlin.clr.ClrIntrinsic("GetCustomAttributes") fun attributes(inherit: Boolean): Array<Any>
}

@kotlin.clr.ClrTypeAlias("System.Array")
private interface ReflectionArrayStorage

private enum class ReflectionKind { CLASS, CLASSIFIER, ANNOTATED, CONTAINER }

private fun reflectionMatches(value: Any?, shape: String, kind: ReflectionKind): Boolean {
    if (shape.length != 0) return value != null && reflectionArrayMatches(value.starProjectionRuntimeType(), shape, 0, kind)
    if (value is NativeReflectionType) return true
    return when (kind) {
        ReflectionKind.CLASS -> value is KClassSlots
        ReflectionKind.CLASSIFIER -> value is KotlinClassifierMarker
        ReflectionKind.ANNOTATED -> value is KAnnotatedElementSlots
        ReflectionKind.CONTAINER -> value is KDeclarationContainerSlots
    }
}

private fun reflectionArrayMatches(type: StarProjectionType, shape: String, index: Int, kind: ReflectionKind): Boolean {
    if (!type.isSzArray) return false
    val component = type.getElementType()!!
    if (index + 1 < shape.length) {
        if (shape[index + 1] == 'o' && component == (ReflectionArrayStorage::class as StarProjectionType)) return true
        return reflectionArrayMatches(component, shape, index + 1, kind)
    }
    return component == (Any::class as StarProjectionType) || reflectionComponentMatches(component, kind)
}

private fun reflectionComponentMatches(component: StarProjectionType, kind: ReflectionKind): Boolean {
    if ((NativeReflectionType::class as StarProjectionType).isAssignableFrom(component)) return true
    return when (kind) {
        ReflectionKind.CLASS -> (KClassSlots::class as StarProjectionType).isAssignableFrom(component)
        ReflectionKind.CLASSIFIER -> (KotlinClassifierMarker::class as StarProjectionType).isAssignableFrom(component)
        ReflectionKind.ANNOTATED -> (KAnnotatedElementSlots::class as StarProjectionType).isAssignableFrom(component)
        ReflectionKind.CONTAINER -> (KDeclarationContainerSlots::class as StarProjectionType).isAssignableFrom(component)
    }
}

@PublishedApi
internal fun kClassMatches(value: Any?, shape: String): Boolean = reflectionMatches(value, shape, ReflectionKind.CLASS)
@PublishedApi
internal fun kClassComponentMatches(component: StarProjectionType): Boolean = reflectionComponentMatches(component, ReflectionKind.CLASS)
@PublishedApi
internal fun kClassClassifier(shape: String): (Any?) -> Boolean = { kClassMatches(it, shape) }

@PublishedApi
internal fun kClassifierMatches(value: Any?, shape: String): Boolean = reflectionMatches(value, shape, ReflectionKind.CLASSIFIER)
@PublishedApi
internal fun kClassifierComponentMatches(component: StarProjectionType): Boolean = reflectionComponentMatches(component, ReflectionKind.CLASSIFIER)
@PublishedApi
internal fun kClassifierClassifier(shape: String): (Any?) -> Boolean = { kClassifierMatches(it, shape) }

@PublishedApi
internal fun annotatedElementMatches(value: Any?, shape: String): Boolean = reflectionMatches(value, shape, ReflectionKind.ANNOTATED)
@PublishedApi
internal fun annotatedElementComponentMatches(component: StarProjectionType): Boolean = reflectionComponentMatches(component, ReflectionKind.ANNOTATED)
@PublishedApi
internal fun annotatedElementClassifier(shape: String): (Any?) -> Boolean = { annotatedElementMatches(it, shape) }

@PublishedApi
internal fun declarationContainerMatches(value: Any?, shape: String): Boolean = reflectionMatches(value, shape, ReflectionKind.CONTAINER)
@PublishedApi
internal fun declarationContainerComponentMatches(component: StarProjectionType): Boolean = reflectionComponentMatches(component, ReflectionKind.CONTAINER)
@PublishedApi
internal fun declarationContainerClassifier(shape: String): (Any?) -> Boolean = { declarationContainerMatches(it, shape) }

@PublishedApi
internal fun kClassSimpleName(value: Any): String? =
    if (value is NativeReflectionType) value.name else (value as KClassSlots).dotktSimpleName()

@PublishedApi
internal fun kClassQualifiedName(value: Any): String? =
    if (value is NativeReflectionType) value.fullName else (value as KClassSlots).dotktQualifiedName()

@PublishedApi
internal fun kClassIsInstance(value: Any, instance: Any?): Boolean =
    if (value is NativeReflectionType) value.isInstance(instance) else (value as KClassSlots).dotktIsInstance(instance)

@PublishedApi
internal fun annotatedElementAnnotations(value: Any): List<Annotation> =
    if (value is NativeReflectionType) value.attributes(false).map { it as Annotation }
    else (value as KAnnotatedElementSlots).dotktAnnotations()

@PublishedApi
internal fun declarationContainerMembers(value: Any): Collection<KCallable<*>> =
    if (value is NativeReflectionType) TODO("CLR reflective member enumeration is not implemented")
    else (value as KDeclarationContainerSlots).dotktMembers()
