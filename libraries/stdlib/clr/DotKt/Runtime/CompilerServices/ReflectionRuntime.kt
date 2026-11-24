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
internal interface KClassSlots {
    fun dotktSimpleName(): String?
    fun dotktQualifiedName(): String?
    fun dotktIsInstance(value: Any?): Boolean
}

@PublishedApi
internal interface RepresentedTypeConstruction

@PublishedApi
internal interface KClassConstruction<T : Any> : RepresentedTypeConstruction

@kotlin.clr.ClrTypeAlias("System.Type")
private interface NativeReflectionType {
    @kotlin.clr.ClrProperty(kotlin.clr.READ, "IsInterface") val isInterface: Boolean
    @kotlin.clr.ClrProperty(kotlin.clr.READ, "Name") val name: String
    @kotlin.clr.ClrProperty(kotlin.clr.READ, "FullName") val fullName: String?
    @kotlin.clr.ClrIntrinsic("IsInstanceOfType") fun isInstance(value: Any?): Boolean
    @kotlin.clr.ClrIntrinsic("GetCustomAttributes") fun attributes(inherit: Boolean): Array<Any>
}

// Only a declared identity applies. A non-generic Kotlin or native subclass must not inherit
// the classifier of its generic base. The token also retains CLR IsInstanceOfType semantics.
internal class KotlinClassifierIdentity(val classifier: StarProjectionType, val declaration: StarProjectionType,
    val sourceName: String?, val local: Boolean, val collection: CollectionLiteralDescriptor? = null)

private class ClassifierCell public constructor() {
    @kotlin.concurrent.Volatile var identity: KotlinClassifierIdentity? = null
}

private val classifierCache = RuntimeTypeCache<StarProjectionType, ClassifierCell>()

private fun classifierIdentity(type: StarProjectionType): KotlinClassifierIdentity {
    val cell = classifierCache.get(type)
    cell.identity?.let { return it }
    // The metadata of a loaded type is immutable. A racing first read may compute the
    // same answer twice; all later lookups, including native misses, avoid reflection.
    val collection = collectionLiteral(type)
    val identity = if (collection == null) readClassifierIdentity(type) ?: KotlinClassifierIdentity(type, type, null, false)
        else KotlinClassifierIdentity(type, type, collection.sourceName, false, collection)
    cell.identity = identity
    return identity
}

@PublishedApi
internal fun runtimeKClass(type: StarProjectionType): StarProjectionType {
    if (!type.isGenericType) return type
    return classifierIdentity(type).classifier
}

@PublishedApi
internal fun reifiedKClass(type: StarProjectionType, shape: Any?): StarProjectionType {
    val source = if (shape is kotlin.reflect.KType) shape.classifier else shape
    return if (source is StarProjectionType && collectionLiteral(source) != null) source
        else if (type.isGenericType) classifierIdentity(type).classifier else type
}

internal fun representedClassStorage(type: StarProjectionType): StarProjectionType =
    if (classifierIdentity(type).collection == null) type else Any::class as StarProjectionType

private fun declarationIdentity(value: Any): KotlinClassifierIdentity {
    if (value !is NativeReflectionType)
        throw UnsupportedOperationException("This KClass does not represent a CLR type")
    return classifierIdentity(value as StarProjectionType)
}

internal fun kClassIsInterface(value: Any): Boolean {
    val identity = declarationIdentity(value)
    val collection = identity.collection
    if (collection != null) return collection.isInterface
    return (identity.declaration as NativeReflectionType).isInterface
}

internal fun kClassCompanionInstance(value: Any): Any? = readCompanionInstance(declarationIdentity(value).declaration)

// A Kotlin generic declaration can use its non-generic classifier as array
// storage. Bounds describe that declaration, not the CLR interfaces of its
// storage face. Preserve closed native types and their exact arguments.
@PublishedApi
internal fun arrayBoundComponent(type: StarProjectionType): StarProjectionType {
    if (type.isGenericType) return type
    val identity = classifierIdentity(type)
    return if (identity.classifier == type) identity.declaration else type
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
internal fun kClassSimpleName(value: Any): String? {
    if (value !is NativeReflectionType) return (value as KClassSlots).dotktSimpleName()
    val name = classifierIdentity(value as StarProjectionType).sourceName ?: return value.name
    return if (name.isEmpty()) null else name.substringAfterLast('.')
}

@PublishedApi
internal fun kClassQualifiedName(value: Any): String? {
    if (value !is NativeReflectionType) return (value as KClassSlots).dotktQualifiedName()
    val identity = classifierIdentity(value as StarProjectionType)
    return if (identity.local) null else identity.sourceName ?: value.fullName
}

@PublishedApi
internal fun kClassIsInstance(value: Any, instance: Any?): Boolean {
    if (value !is NativeReflectionType) return (value as KClassSlots).dotktIsInstance(instance)
    val token = value as StarProjectionType
    classifierIdentity(token).collection?.let { return it.accepts(instance) }
    if (instance != null && token.isGenericTypeDefinition && classifierIdentity(token).local)
        return starProjectionHasView(instance.starProjectionRuntimeType(), token)
    return value.isInstance(instance)
}

@PublishedApi
internal fun annotatedElementAnnotations(value: Any): List<Annotation> =
    if (value is NativeReflectionType) (classifierIdentity(value as StarProjectionType).declaration as NativeReflectionType)
        .attributes(false).map { it as Annotation }
    else (value as KAnnotatedElementSlots).dotktAnnotations()

@PublishedApi
internal fun declarationContainerMembers(value: Any): Collection<KCallable<*>> =
    if (value is NativeReflectionType) TODO("CLR reflective member enumeration is not implemented")
    else (value as KDeclarationContainerSlots).dotktMembers()
