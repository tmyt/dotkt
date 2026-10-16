@file:Suppress("UNCHECKED_CAST")

package DotKt.Runtime.CompilerServices

import kotlin.collections.ClrRawEnumerator

@kotlin.clr.ClrTypeAlias("System.IDisposable")
private interface EqualityDisposable {
    @kotlin.clr.ClrIntrinsic("Dispose")
    fun dispose()
}

private fun disposeEnumerator(enumerator: ClrRawEnumerator) {
    if (enumerator is EqualityDisposable) enumerator.dispose()
}

// Only trusted class-alias declarations supply a structural default. Merely
// implementing a List/Set/Map interface does not override Any.equals.
@kotlin.clr.ClrIntrinsic("dotkt.equalityStorage.List")
private fun listEqualityStorageTypes(): Array<StarProjectionType> = error("compiler intrinsic")
@kotlin.clr.ClrIntrinsic("dotkt.equalityStorage.Set")
private fun setEqualityStorageTypes(): Array<StarProjectionType> = error("compiler intrinsic")
@kotlin.clr.ClrIntrinsic("dotkt.equalityStorage.Map")
private fun mapEqualityStorageTypes(): Array<StarProjectionType> = error("compiler intrinsic")

private val listEqualityStorage = listEqualityStorageTypes()
private val setEqualityStorage = setEqualityStorageTypes()
private val mapEqualityStorage = mapEqualityStorageTypes()

@kotlin.clr.ClrTypeAlias("System.Object")
private interface NativeEqualityObject {
    @kotlin.clr.ClrIntrinsic("Equals")
    fun nativeEquals(other: Any?): Boolean
}

private fun Any.nativeObjectEquals(other: Any?): Boolean = (this as NativeEqualityObject).nativeEquals(other)

private val objectType = Any().starProjectionRuntimeType()
private val objectEqualsSlot = findObjectEqualsSlot()

private fun findObjectEqualsSlot(): StarProjectionMethod {
    for (method in objectType.getMethods()) {
        if (!method.isStatic && method.isVirtual && method.name.nativeObjectEquals("Equals")
            && method.getParameters().size == 1
            && method.getParameters()[0].parameterType.nativeObjectEquals(objectType)) return method
    }
    error("Missing System.Object.Equals slot")
}

private fun hasNativeEqualsOverride(type: StarProjectionType): Boolean {
    for (method in type.getMethods()) {
        if (!method.isVirtual) continue
        val root = method.getBaseDefinition()
        if (root.metadataToken == objectEqualsSlot.metadataToken
            && root.module.nativeObjectEquals(objectEqualsSlot.module)
            && !method.declaringType!!.nativeObjectEquals(objectType)) return true
    }
    return false
}

/** Null-safe Kotlin structural equality. Arguments are evaluated once by the caller. */
@PublishedApi
internal fun kotlinEquals(left: Any?, right: Any?): Boolean =
    if (left === null) right === null else kotlinInstanceEquals(left, right)

/** An instance call differs from == only in its null-receiver behavior. */
@PublishedApi
internal fun kotlinInstanceEquals(left: Any?, right: Any?): Boolean {
    val receiver = left!!
    // Virtual dispatch precedes even the self/null shortcuts: authored equals
    // implementations may observe those calls or deliberately return false.
    if (receiver is String) return receiver.nativeObjectEquals(right)
    if (receiver is Double) return right is Double && kotlin.clrDoubleEquals(receiver, right)
    if (receiver is Float) return right is Float && kotlin.clrFloatEquals(receiver, right)
    val type = receiver.starProjectionRuntimeType()
    if (type.isValueType || hasNativeEqualsOverride(type)) return receiver.nativeObjectEquals(right)
    var storage: StarProjectionType? = type
    while (storage != null) {
        val definition = if (storage.isGenericType) storage.getGenericTypeDefinition() else storage
        for (candidate in listEqualityStorage)
            if (definition.nativeObjectEquals(candidate)) return structuralEquals(receiver, right, 0, storage)
        for (candidate in setEqualityStorage)
            if (definition.nativeObjectEquals(candidate)) return structuralEquals(receiver, right, 1, storage)
        for (candidate in mapEqualityStorage)
            if (definition.nativeObjectEquals(candidate)) return structuralEquals(receiver, right, 2, storage)
        storage = storage.baseType
    }
    return receiver.nativeObjectEquals(right)
}

private fun equalityView(value: Any, family: Int, storage: StarProjectionType?): StarProjectionType? {
    if (storage == null) {
        var base: StarProjectionType? = value.starProjectionRuntimeType()
        val definitions = when (family) { 0 -> listEqualityStorage; 1 -> setEqualityStorage; else -> mapEqualityStorage }
        while (base != null) {
            val definition = if (base.isGenericType) base.getGenericTypeDefinition() else base
            for (candidate in definitions)
                if (definition.nativeObjectEquals(candidate)) return equalityView(value, family, base)
            base = base.baseType
        }
    }
    if (family == 1 && storage == null) return projectedSetFamily(value, null, failOnAmbiguity = false)
    val preferred = when (family) {
        0 -> "System.Collections.Generic.IReadOnlyList`1"
        1 -> "System.Collections.Generic.IReadOnlySet`1"
        else -> "System.Collections.Generic.IReadOnlyDictionary`2"
    }
    val fallback = when (family) {
        0 -> "System.Collections.Generic.IList`1"
        1 -> "System.Collections.Generic.ISet`1"
        else -> "System.Collections.Generic.IDictionary`2"
    }
    if (storage == null) return findErasedProjectedView(value, preferred, fallback, failOnAmbiguity = false)
    // An inherited bound-class default uses that base's own storage contract,
    // not an unrelated interface reimplemented by the derived CLR class.
    var preferredView: StarProjectionType? = null
    var fallbackView: StarProjectionType? = null
    for (view in storage.getInterfaces()) {
        if (!view.isGenericType) continue
        val name = view.getGenericTypeDefinition().fullName
        if (name == preferred) preferredView = view
        if (name == fallback) fallbackView = view
    }
    return preferredView ?: fallbackView
}

private fun equalityCount(value: Any, view: StarProjectionType): Int {
    val name = view.getGenericTypeDefinition().fullName!!
    return projectedParentCollectionCount(value, view,
        if (name == "System.Collections.Generic.IReadOnlyList`1"
            || name == "System.Collections.Generic.IReadOnlySet`1"
            || name == "System.Collections.Generic.IReadOnlyDictionary`2"
            || name == "System.Collections.Generic.IReadOnlyCollection`1")
            "System.Collections.Generic.IReadOnlyCollection`1" else "System.Collections.Generic.ICollection`1")
}

private fun equalityEnumerator(value: Any, view: StarProjectionType): ClrRawEnumerator =
    projectedFamilyEnumerator(value, view) as ClrRawEnumerator

private fun entryPart(entry: Any, name: String): Any? = try {
    erasedProjectedMethod(entry.starProjectionRuntimeType(), name, 0).invoke(entry, emptyArray<Any?>())
} catch (failure: StarProjectionInvocationException) {
    throw (failure.innerException ?: failure)
}

// Real emitted stdlib implementations call this from their virtual equals slot.
// Arbitrary Kotlin implementers keep their own inherited/authored Any contract.
@PublishedApi
internal fun structuralEquals(left: Any, right: Any?, family: Int, storage: StarProjectionType? = null): Boolean {
    if (left === right) return true
    if (right === null || right.starProjectionRuntimeType().isArray) return false
    val leftView = equalityView(left, family, storage) ?: return false
    val rightView = equalityView(right, family, null) ?: return false
    if (equalityCount(left, leftView) != equalityCount(right, rightView)) return false
    val first = equalityEnumerator(left, leftView)
    try {
        if (family == 0) {
            val second = equalityEnumerator(right, rightView)
            try {
                while (first.MoveNext()) {
                    if (!second.MoveNext() || !kotlinEquals(first.current(), second.current())) return false
                }
                return !second.MoveNext()
            } finally { disposeEnumerator(second) }
        }
        while (first.MoveNext()) {
            val element = first.current()
            val second = equalityEnumerator(right, rightView)
            var found = false
            try {
                while (second.MoveNext()) {
                    val candidate = second.current()
                    if (family == 1) {
                        if (kotlinEquals(element, candidate)) { found = true; break }
                    } else if (kotlinEquals(entryPart(element!!, "get_Key"), entryPart(candidate!!, "get_Key"))) {
                        if (!kotlinEquals(entryPart(element, "get_Value"), entryPart(candidate, "get_Value"))) return false
                        found = true
                        break
                    }
                }
            } finally { disposeEnumerator(second) }
            if (!found) return false
        }
        return true
    } finally {
        disposeEnumerator(first)
    }
}
