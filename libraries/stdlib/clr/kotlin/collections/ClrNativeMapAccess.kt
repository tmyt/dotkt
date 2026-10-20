@file:Suppress("UNCHECKED_CAST")

package kotlin.collections

import DotKt.Runtime.CompilerServices.StarProjectionType
import DotKt.Runtime.CompilerServices.StarProjectionMethod
import DotKt.Runtime.CompilerServices.StarProjectionInvocationException
import DotKt.Runtime.CompilerServices.starProjectionRuntimeType
import DotKt.Runtime.CompilerServices.starProjectionTypeAcceptsNull
import DotKt.Runtime.CompilerServices.erasedProjectedMethod
import DotKt.Runtime.CompilerServices.projectedFamilyEnumerator
import DotKt.Runtime.CompilerServices.projectedParentCollectionCount

@kotlin.clr.ClrTypeAlias("System.IDisposable")
private interface ClrMapDisposable {
    @kotlin.clr.ClrIntrinsic("Dispose") fun dispose()
}

private class ClrNativeMapViewPreference(val key: StarProjectionType?, val keyVariance: Int,
    val value: StarProjectionType?, val valueVariance: Int) {
    fun exact(arguments: Array<StarProjectionType>): Boolean = key == arguments[0] && value == arguments[1]
    private fun compatible(bound: StarProjectionType?, variance: Int, actual: StarProjectionType): Boolean =
        bound == null || when (variance) {
            1 -> bound.isAssignableFrom(actual)
            -1 -> actual.isAssignableFrom(bound)
            else -> bound == actual
        }
    fun compatible(arguments: Array<StarProjectionType>): Boolean =
        compatible(key, keyVariance, arguments[0]) && compatible(value, valueVariance, arguments[1])
}

// CLR representation vocabulary supplied by bir2cir. A null argument is a star/unknown
// position; it is not System.Object. Variance is invariant=0, out=1, in=-1.
@PublishedApi
internal fun clrMapViewPreference(key: Any?, keyVariance: Int, value: Any?, valueVariance: Int): Any =
    ClrNativeMapViewPreference(key as StarProjectionType?, keyVariance, value as StarProjectionType?, valueVariance)

// A view is a preference, not an invariant cast. Preserve partial source constraints and
// choose an exact or uniquely compatible implemented face; a sole actual face remains usable
// through Kotlin's erased views. Argument values and reflection order never choose a face.
private fun nativeMapView(source: Any, preferred: ClrNativeMapViewPreference?): StarProjectionType? {
    var match: StarProjectionType? = null
    var ambiguous = false
    var compatible: StarProjectionType? = null
    var compatibleAmbiguous = false
    for (face in source.starProjectionRuntimeType().getInterfaces()) {
        if (!face.isGenericType || face.getGenericTypeDefinition().fullName != "System.Collections.Generic.IDictionary`2") continue
        val arguments = face.getGenericArguments()
        if (preferred != null && preferred.exact(arguments)) return face
        if (match != null && match != face) ambiguous = true
        match = face
        if (preferred == null || preferred.compatible(arguments)) {
            if (compatible != null && compatible != face) compatibleAmbiguous = true
            compatible = face
        }
    }
    if (compatibleAmbiguous) throw IllegalStateException("Ambiguous projected dictionary view")
    if (compatible != null) return compatible
    if (ambiguous) throw IllegalStateException("Ambiguous projected dictionary view")
    return match
}

private fun invokeMap(method: StarProjectionMethod, source: Any, arguments: Array<Any?>): Any? =
    try { method.invoke(source, arguments) }
    catch (failure: StarProjectionInvocationException) { throw (failure.innerException ?: failure) }

/** Operation-local dispatch plan. The original map, not this plan, crosses Kotlin value boundaries. */
internal class ClrNativeMapAccess(private val source: Any, preferred: Any?) {
    // Retain the existing variance-independent raw dictionary path where it exists.
    // Only generic-only implementations need an existential interface dispatch plan.
    private val raw: ClrRawDictionary? = source as? ClrRawDictionary
    private val view: StarProjectionType? = if (raw != null) null
        else nativeMapView(source, preferred as ClrNativeMapViewPreference?)

    fun count(): Int = if (view == null) (source as ClrRawCollection).count()
        else projectedParentCollectionCount(source, view, "System.Collections.Generic.ICollection`1")

    private fun accepts(type: StarProjectionType, value: Any?): Boolean =
        if (value == null) starProjectionTypeAcceptsNull(type)
        else type.isAssignableFrom(value.starProjectionRuntimeType())

    fun Contains(key: Any?): Boolean {
        if (view == null) return raw!!.Contains(key)
        if (!accepts(view.getGenericArguments()[0], key)) return false
        return invokeMap(erasedProjectedMethod(view, "ContainsKey", 1), source, arrayOf(key)) as Boolean
    }

    fun rawGet(key: Any?): Any? {
        if (view == null) return raw!!.rawGet(key)
        return invokeMap(erasedProjectedMethod(view, "get_Item", 1), source, arrayOf(key))
    }

    fun rawSet(key: Any?, value: Any?) {
        if (view == null) { raw!!.rawSet(key, value); return }
        val arguments = view.getGenericArguments()
        if (!accepts(arguments[0], key) || !accepts(arguments[1], value))
            throw ClassCastException("Value does not fit the native dictionary contract")
        invokeMap(erasedProjectedMethod(view, "set_Item", 2), source, arrayOf(key, value))
    }

    fun rawRemove(key: Any?) {
        if (view == null) { raw!!.rawRemove(key); return }
        if (accepts(view.getGenericArguments()[0], key))
            invokeMap(erasedProjectedMethod(view, "Remove", 1), source, arrayOf(key))
    }

    fun rawClear() {
        if (view == null) { raw!!.rawClear(); return }
        // Clear/Count belong to ICollection<KeyValuePair<K,V>>, not to an unrelated
        // ICollection interface which the same receiver may also implement.
        for (parent in view.getInterfaces()) {
            if (parent.isGenericType && parent.getGenericTypeDefinition().fullName == "System.Collections.Generic.ICollection`1") {
                invokeMap(erasedProjectedMethod(parent, "Clear", 0), source, emptyArray())
                return
            }
        }
        throw IllegalStateException("Dictionary has no collection parent")
    }

    fun GetEnumerator(): ClrNativeMapCursor = if (view == null) ClrNativeMapCursor(raw!!.GetEnumerator(), false)
        else ClrNativeMapCursor(projectedFamilyEnumerator(source, view), true)
}

internal class ClrNativeMapCursor(private val enumerator: Any, private val generic: Boolean) {
    fun MoveNext(): Boolean = if (generic) (enumerator as ClrRawEnumerator).MoveNext()
        else (enumerator as ClrRawDictionaryEnumerator).MoveNext()
    private fun pair(): Any = (enumerator as ClrRawEnumerator).current()!!
    fun key(): Any? {
        if (!generic) return (enumerator as ClrRawDictionaryEnumerator).key()
        val entry = pair()
        return invokeMap(erasedProjectedMethod(entry.starProjectionRuntimeType(), "get_Key", 0), entry, emptyArray())
    }
    fun value(): Any? {
        if (!generic) return (enumerator as ClrRawDictionaryEnumerator).value()
        val entry = pair()
        return invokeMap(erasedProjectedMethod(entry.starProjectionRuntimeType(), "get_Value", 0), entry, emptyArray())
    }
    fun close() { if (enumerator is ClrMapDisposable) enumerator.dispose() }
}
