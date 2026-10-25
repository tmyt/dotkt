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

    @kotlin.clr.ClrIntrinsic("GetHashCode")
    fun nativeHashCode(): Int
}

private fun Any.nativeObjectEquals(other: Any?): Boolean = (this as NativeEqualityObject).nativeEquals(other)

@kotlin.clr.ClrIntrinsic("System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode")
private fun equalityIdentityHash(value: Any): Int = error("CLR binding")

@PublishedApi
internal fun kotlinHashCode(value: Any?): Int {
    if (value === null) return 0
    if (value is String || value.starProjectionRuntimeType().isValueType)
        return (value as NativeEqualityObject).nativeHashCode()
    val plan = equalityTypePlan(value.starProjectionRuntimeType())
    return if (plan.family < 0 || plan.nativeHashOverride) (value as NativeEqualityObject).nativeHashCode()
        else structuralHash(value, plan.family, plan.storage)
}

@PublishedApi
internal fun kotlinInstanceHashCode(value: Any?): Int = kotlinHashCode(value!!)

@PublishedApi
internal fun structuralHash(value: Any, family: Int, storage: StarProjectionType? = null): Int {
    val view = equalityView(value, family, storage) ?: return equalityIdentityHash(value)
    val elements = equalityEnumerator(value, view)
    var hash = if (family == 0) 1 else 0
    try {
        while (elements.MoveNext()) {
            val element = elements.current()
            if (family == 2) {
                val entry = element!!
                val access = equalityEntryAccess(entry.starProjectionRuntimeType())
                hash += kotlinHashCode(invokeEquality(access.key, entry, equalityNoArguments)) xor
                    kotlinHashCode(invokeEquality(access.value, entry, equalityNoArguments))
            } else {
                val elementHash = kotlinHashCode(element)
                hash = if (family == 0) 31 * hash + elementHash else hash + elementHash
            }
        }
        return hash
    } finally { disposeEnumerator(elements) }
}

private val objectType = Any().starProjectionRuntimeType()
private val objectEqualsSlot = findObjectEqualsSlot()
private val objectHashSlot = findObjectHashSlot()

private fun findObjectHashSlot(): StarProjectionMethod {
    for (method in objectType.getMethods()) {
        if (!method.isStatic && method.isVirtual && method.name.nativeObjectEquals("GetHashCode")
            && method.getParameters().size == 0) return method
    }
    error("Missing System.Object.GetHashCode slot")
}

private fun findObjectEqualsSlot(): StarProjectionMethod {
    for (method in objectType.getMethods()) {
        if (!method.isStatic && method.isVirtual && method.name.nativeObjectEquals("Equals")
            && method.getParameters().size == 1
            && method.getParameters()[0].parameterType.nativeObjectEquals(objectType)) return method
    }
    error("Missing System.Object.Equals slot")
}

private fun hasNativeEqualsOverride(type: StarProjectionType): Boolean {
    return hasNativeObjectOverride(type, objectEqualsSlot)
}

private fun hasNativeObjectOverride(type: StarProjectionType, slot: StarProjectionMethod): Boolean {
    for (method in type.getMethods()) {
        if (!method.isVirtual) continue
        val root = method.getBaseDefinition()
        if (root.metadataToken == slot.metadataToken
            && root.module.nativeObjectEquals(slot.module)
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
    if (type.isValueType) return receiver.nativeObjectEquals(right)
    val plan = equalityTypePlan(type)
    return if (plan.family < 0) receiver.nativeObjectEquals(right)
        else structuralEquals(receiver, right, plan.family, plan.storage)
}

internal fun classifyEqualityType(type: StarProjectionType): EqualityTypePlan {
    if (hasNativeEqualsOverride(type)) return EqualityTypePlan(-1, null, true)
    var storage: StarProjectionType? = type
    while (storage != null) {
        val definition = if (storage.isGenericType) storage.getGenericTypeDefinition() else storage
        for (candidate in listEqualityStorage)
            if (definition.nativeObjectEquals(candidate)) return EqualityTypePlan(0, storage, hasNativeObjectOverride(type, objectHashSlot))
        for (candidate in setEqualityStorage)
            if (definition.nativeObjectEquals(candidate)) return EqualityTypePlan(1, storage, hasNativeObjectOverride(type, objectHashSlot))
        for (candidate in mapEqualityStorage)
            if (definition.nativeObjectEquals(candidate)) return EqualityTypePlan(2, storage, hasNativeObjectOverride(type, objectHashSlot))
        storage = storage.baseType
    }
    return EqualityTypePlan(-1, null, true)
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
    // The bound base fixes the family and exact interface identity. Native
    // reimplementation of THAT interface remains virtual; unrelated faces do
    // not change the default. Do not reconstruct private native storage.
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

private val equalityNoArguments = emptyArray<Any?>()

private fun invokeEquality(method: StarProjectionMethod, receiver: Any, arguments: Array<Any?>): Any? = try {
    method.invoke(receiver, arguments)
} catch (failure: StarProjectionInvocationException) {
    throw (failure.innerException ?: failure)
}

private fun mapStructuralEquals(left: Any, leftView: StarProjectionType, right: Any, rightView: StarProjectionType): Boolean {
    // Common AbstractMap compares the peer's entries through this map's lookup,
    // preserving authored/native lookup semantics without a Cartesian scan.
    val contains = erasedProjectedMethod(leftView, "ContainsKey", 1)
    val get = erasedProjectedMethod(leftView, "get_Item", 1)
    val keyType = contains.getParameters()[0].parameterType
    val entries = equalityEnumerator(right, rightView)
    try {
        while (entries.MoveNext()) {
            val entry = entries.current()!!
            val access = equalityEntryAccess(entry.starProjectionRuntimeType())
            val key = invokeEquality(access.key, entry, equalityNoArguments)
            if (key === null) {
                if (!starProjectionTypeAcceptsNull(keyType)) return false
                // A nullable CLR key slot does not promise native null-key lookup.
                // Inspect the selected public entry contract for this one value;
                // authored Kotlin maps keep their own lookup/override semantics.
                if (left !is KotlinMapDefaultSlots) {
                    val own = equalityEnumerator(left, leftView)
                    var found = false
                    try {
                        while (own.MoveNext()) {
                            val candidate = own.current()!!
                            val candidateAccess = equalityEntryAccess(candidate.starProjectionRuntimeType())
                            if (invokeEquality(candidateAccess.key, candidate, equalityNoArguments) === null) {
                                if (!kotlinEquals(invokeEquality(candidateAccess.value, candidate, equalityNoArguments),
                                        invokeEquality(access.value, entry, equalityNoArguments))) return false
                                found = true
                                break
                            }
                        }
                    } finally { disposeEnumerator(own) }
                    if (!found) return false
                    continue
                }
            } else if (!keyType.isAssignableFrom(key.starProjectionRuntimeType())) return false
            val arguments = arrayOf(key)
            if (!(invokeEquality(contains, left, arguments) as Boolean)) return false
            if (!kotlinEquals(invokeEquality(get, left, arguments),
                    invokeEquality(access.value, entry, equalityNoArguments))) return false
        }
        return true
    } finally { disposeEnumerator(entries) }
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
    if (family == 2) return mapStructuralEquals(left, leftView, right, rightView)
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
        val enumerateSecond = projectedFamilyEnumeratorMethod(rightView)
        while (first.MoveNext()) {
            val element = first.current()
            val second = invokeEquality(enumerateSecond, right, equalityNoArguments) as ClrRawEnumerator
            var found = false
            try {
                while (second.MoveNext()) {
                    val candidate = second.current()
                    if (kotlinEquals(element, candidate)) { found = true; break }
                }
            } finally { disposeEnumerator(second) }
            if (!found) return false
        }
        return true
    } finally {
        disposeEnumerator(first)
    }
}
