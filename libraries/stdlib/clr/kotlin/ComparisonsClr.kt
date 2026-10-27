/*
 * CLR raw-comparable binding for kotlin.comparisons.
 */

package kotlin.comparisons

import DotKt.Runtime.CompilerServices.StarProjectionInvocationException
import DotKt.Runtime.CompilerServices.StarProjectionType
import DotKt.Runtime.CompilerServices.starProjectionHasView
import DotKt.Runtime.CompilerServices.starProjectionRuntimeType
import DotKt.Runtime.CompilerServices.starProjectionTypeAcceptsNull

@kotlin.clr.ClrTypeAlias("System.IComparable")
internal interface ClrRawComparable {
    @kotlin.clr.ClrIntrinsic("CompareTo")
    fun compareTo(other: Any?): Int
}

@kotlin.clr.ClrIntrinsic("System.Type.GetType")
private fun comparisonRuntimeType(name: String): StarProjectionType = error("CLR binding")

private val genericComparisonDefinition = comparisonRuntimeType("System.IComparable`1")

@kotlin.clr.ClrTypeAlias("System.Array")
private interface ComparisonArrayStorage

@PublishedApi
internal fun clrComparableMatches(value: Any?, arrayShape: String): Boolean =
    if (arrayShape.length == 0) value != null && clrComparableComponentMatches(value.starProjectionRuntimeType())
    else value != null && comparisonArrayTypeMatches(value.starProjectionRuntimeType(), arrayShape, 0)

private fun comparisonArrayTypeMatches(type: StarProjectionType, shape: String, index: Int): Boolean {
    if (!type.isSzArray) return false
    val component = type.getElementType()!!
    if (index + 1 < shape.length) {
        if (shape[index + 1] == 'o' && component == (ComparisonArrayStorage::class as StarProjectionType)) return true
        return comparisonArrayTypeMatches(component, shape, index + 1)
    }
    return component == (Any::class as StarProjectionType) || clrComparableComponentMatches(component)
}

@PublishedApi
internal fun clrComparableComponentMatches(component: StarProjectionType): Boolean =
    (ClrRawComparable::class as StarProjectionType).isAssignableFrom(component) ||
        starProjectionHasView(component, genericComparisonDefinition)

@PublishedApi
internal fun clrComparableClassifier(arrayShape: String): (Any?) -> Boolean = { clrComparableMatches(it, arrayShape) }

@PublishedApi
internal fun clrErasedCompareTo(a: Any, b: Any?): Int {
    // Preserve native erased dispatch, including authored bridges and its exceptions.
    if (a is ClrRawComparable) return a.compareTo(b)

    // compareValues has no exact closed Comparable witness. A generic-only CLR
    // value is comparable here only when exactly one interface accepts b.
    val argumentType = b?.starProjectionRuntimeType()
    var selected: StarProjectionType? = null
    for (face in a.starProjectionRuntimeType().getInterfaces()) {
        if (!face.isGenericType || face.getGenericTypeDefinition() != genericComparisonDefinition) continue
        val parameter = face.getGenericArguments()[0]
        if (argumentType == null) {
            if (!starProjectionTypeAcceptsNull(parameter)) continue
        } else if (!parameter.isAssignableFrom(argumentType)) continue
        if (selected != null) throw ClassCastException("Ambiguous CLR comparison interfaces")
        selected = face
    }
    val view = selected ?: throw ClassCastException("No applicable CLR comparison interface")
    // Reflect the interface slot, not a same-named public method on the class:
    // explicit implementations and inherited implementations must both work.
    for (method in view.getMethods()) {
        if (method.name != "CompareTo" || method.isStatic || method.getParameters().size != 1) continue
        return try {
            method.invoke(a, arrayOf<Any?>(b)) as Int
        } catch (failure: StarProjectionInvocationException) {
            throw (failure.innerException ?: failure)
        }
    }
    error("Missing CLR comparison slot")
}
