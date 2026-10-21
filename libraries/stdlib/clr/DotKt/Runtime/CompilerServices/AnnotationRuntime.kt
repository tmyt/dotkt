package DotKt.Runtime.CompilerServices

@PublishedApi
internal interface KotlinAnnotationMarker

@kotlin.clr.ClrTypeAlias("System.Attribute")
private interface NativeAnnotation

@kotlin.clr.ClrTypeAlias("System.Array")
private interface AnnotationArrayStorage

@PublishedApi
internal fun annotationMatches(value: Any?, arrayShape: String): Boolean {
    if (arrayShape.length == 0) return value is NativeAnnotation || value is KotlinAnnotationMarker
    return value != null && annotationArrayTypeMatches(value.starProjectionRuntimeType(), arrayShape, 0)
}

private fun annotationArrayTypeMatches(type: StarProjectionType, shape: String, index: Int): Boolean {
    if (!type.isSzArray) return false
    val component = type.getElementType()!!
    if (index + 1 < shape.length) {
        if (shape[index + 1] == 'o' && component == (AnnotationArrayStorage::class as StarProjectionType)) return true
        return annotationArrayTypeMatches(component, shape, index + 1)
    }
    return component == (Any::class as StarProjectionType) || annotationComponentMatches(component)
}

@PublishedApi
internal fun annotationComponentMatches(component: StarProjectionType): Boolean =
    (NativeAnnotation::class as StarProjectionType).isAssignableFrom(component) ||
        (KotlinAnnotationMarker::class as StarProjectionType).isAssignableFrom(component)

@PublishedApi
internal fun annotationClassifier(arrayShape: String): (Any?) -> Boolean = { annotationMatches(it, arrayShape) }
