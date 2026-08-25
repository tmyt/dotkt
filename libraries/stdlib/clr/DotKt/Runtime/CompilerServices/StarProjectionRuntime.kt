@file:Suppress("UNCHECKED_CAST")

package DotKt.Runtime.CompilerServices

// Minimal CLR reflection vocabulary for the runtime implementation below. The stdlib frontend is compiled without
// projected BCL KLIBs, so these follow the same @ClrTypeAlias pattern as the array runtime helpers. They are erased to
// the named BCL types and never become DotKt runtime classifiers of their own.
@kotlin.clr.ClrTypeAlias("System.Reflection.Module")
@PublishedApi
internal interface StarProjectionModule

@kotlin.clr.ClrTypeAlias("System.Reflection.MethodInfo")
@PublishedApi
internal interface StarProjectionMethod {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Name")
    val name: String

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "DeclaringType")
    val declaringType: StarProjectionType?

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "MetadataToken")
    val metadataToken: Int

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Module")
    val module: StarProjectionModule

    @kotlin.clr.ClrIntrinsic("MakeGenericMethod")
    fun makeGenericMethod(typeArguments: Array<StarProjectionType>): StarProjectionMethod

    @kotlin.clr.ClrIntrinsic("Invoke")
    fun invoke(receiver: Any?, arguments: Array<Any?>): Any?

    @kotlin.clr.ClrIntrinsic("GetGenericArguments")
    fun getGenericArguments(): Array<StarProjectionType>

    @kotlin.clr.ClrIntrinsic("GetParameters")
    fun getParameters(): Array<StarProjectionParameter>
}

@kotlin.clr.ClrTypeAlias("System.Reflection.ParameterInfo")
@PublishedApi
internal interface StarProjectionParameter {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "ParameterType")
    val parameterType: StarProjectionType
}

@kotlin.clr.ClrTypeAlias("System.Reflection.FieldInfo")
@PublishedApi
internal interface StarProjectionField {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Name")
    val name: String

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "DeclaringType")
    val declaringType: StarProjectionType?

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "MetadataToken")
    val metadataToken: Int

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Module")
    val module: StarProjectionModule

    @kotlin.clr.ClrIntrinsic("GetValue")
    fun getValue(receiver: Any?): Any?

    @kotlin.clr.ClrIntrinsic("SetValue")
    fun setValue(receiver: Any?, value: Any?)
}

@kotlin.clr.ClrTypeAlias("System.Type")
@PublishedApi
internal interface StarProjectionType {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "IsGenericType")
    val isGenericType: Boolean

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "IsGenericTypeDefinition")
    val isGenericTypeDefinition: Boolean

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "IsGenericParameter")
    val isGenericParameter: Boolean

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "GenericParameterPosition")
    val genericParameterPosition: Int

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "DeclaringMethod")
    val declaringMethod: Any?

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "IsArray")
    val isArray: Boolean

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "IsByRef")
    val isByRef: Boolean

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "BaseType")
    val baseType: StarProjectionType?

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "FullName")
    val fullName: String?

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Module")
    val module: StarProjectionModule

    @kotlin.clr.ClrIntrinsic("GetGenericTypeDefinition")
    fun getGenericTypeDefinition(): StarProjectionType

    @kotlin.clr.ClrIntrinsic("GetGenericArguments")
    fun getGenericArguments(): Array<StarProjectionType>

    @kotlin.clr.ClrIntrinsic("GetElementType")
    fun getElementType(): StarProjectionType?

    @kotlin.clr.ClrIntrinsic("GetArrayRank")
    fun getArrayRank(): Int

    @kotlin.clr.ClrIntrinsic("GetMethods")
    fun getMethods(): Array<StarProjectionMethod>

    @kotlin.clr.ClrIntrinsic("GetFields")
    fun getFields(): Array<StarProjectionField>

    @kotlin.clr.ClrIntrinsic("GetInterfaces")
    fun getInterfaces(): Array<StarProjectionType>
}

@kotlin.clr.ClrTypeAlias("System.Reflection.TargetInvocationException")
@PublishedApi
internal open class StarProjectionInvocationException : Throwable() {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "InnerException")
    val innerException: Throwable? get() = null
}

@kotlin.clr.ClrIntrinsic("GetType")
private fun Any.starProjectionRuntimeType(): StarProjectionType = TODO("clr binding should be implemented")

// CLR generics are reified, so an arbitrary foreign G<X> has no nominal type that can represent Kotlin's G<*>.
// bir2cir keeps the value itself (no wrapper) and calls this runtime only for classifier tests/casts and member
// dispatch through that existential view. The compiler supplies the exact open declaring type and metadata token;
// this code never resolves an overload by source name.
@PublishedApi
internal fun starProjectionIsInstance(value: Any?, openGenericType: StarProjectionType): Boolean =
    value != null && starProjectionClosedView(value.starProjectionRuntimeType(), openGenericType) != null

@PublishedApi
internal fun starProjectionCast(value: Any?, openGenericType: StarProjectionType): Any {
    if (value != null && starProjectionClosedView(value.starProjectionRuntimeType(), openGenericType) != null) return value
    throw ClassCastException("Value is not an instance of " + openGenericType.fullName)
}

@PublishedApi
internal fun starProjectionSafeCast(value: Any?, openGenericType: StarProjectionType): Any? =
    if (value != null && starProjectionClosedView(value.starProjectionRuntimeType(), openGenericType) != null) value else null

// Collection/Set use overlapping BCL faces for member dispatch, so their Kotlin classifier is a composite physical
// fact. Emitted Kotlin implementations carry the nominal identities above; unmodifiable BCL-backed implementations
// are recognized by the generic faces they actually implement. Dictionary and array shapes are deliberately excluded
// from Collection: both implement collection-shaped CLR interfaces but neither is a Kotlin Collection.
@PublishedApi
internal fun starProjectionKotlinCollectionIsInstance(
    value: Any?,
    kind: Int,
    firstOpenType: StarProjectionType,
    secondOpenType: StarProjectionType,
    dictionaryOpenType: StarProjectionType,
    readOnlyDictionaryOpenType: StarProjectionType,
): Boolean {
    if (value == null) return false
    if (kind == 2 && value is KotlinMutableSetClassifier) return true
    if (kind == 1 && value is KotlinSetClassifier) return true
    if (kind == 0 && value is KotlinCollectionClassifier) return true
    val runtimeType = value.starProjectionRuntimeType()
    if (kind == 0 && (runtimeType.isArray
            || starProjectionHasView(runtimeType, dictionaryOpenType)
            || starProjectionHasView(runtimeType, readOnlyDictionaryOpenType))) return false
    return starProjectionHasView(runtimeType, firstOpenType) || starProjectionHasView(runtimeType, secondOpenType)
}

@PublishedApi
internal fun starProjectionKotlinNullableCollectionIsInstance(
    value: Any?,
    kind: Int,
    firstOpenType: StarProjectionType,
    secondOpenType: StarProjectionType,
    dictionaryOpenType: StarProjectionType,
    readOnlyDictionaryOpenType: StarProjectionType,
): Boolean = value == null || starProjectionKotlinCollectionIsInstance(value, kind, firstOpenType, secondOpenType,
    dictionaryOpenType, readOnlyDictionaryOpenType)

@PublishedApi
internal fun starProjectionKotlinCollectionCast(
    value: Any?,
    kind: Int,
    firstOpenType: StarProjectionType,
    secondOpenType: StarProjectionType,
    dictionaryOpenType: StarProjectionType,
    readOnlyDictionaryOpenType: StarProjectionType,
): Any {
    if (starProjectionKotlinCollectionIsInstance(value, kind, firstOpenType, secondOpenType,
            dictionaryOpenType, readOnlyDictionaryOpenType)) return value!!
    throw ClassCastException("Value is not an instance of the requested Kotlin collection classifier")
}

@PublishedApi
internal fun starProjectionCloneValue(value: Any): Any = starProjectionCloneValueIntrinsic(value)

@kotlin.clr.ClrIntrinsic("System.Runtime.CompilerServices.RuntimeHelpers.GetObjectValue")
private fun starProjectionCloneValueIntrinsic(value: Any): Any = TODO("clr binding should be implemented")

@PublishedApi
internal fun starProjectionInvoke(
    receiver: Any,
    openGenericType: StarProjectionType,
    closedViewHint: StarProjectionType?,
    metadataToken: Int,
    memberName: String,
    methodArity: Int,
    parameterTypeKeys: Array<String>,
    methodTypeArguments: Array<StarProjectionType>,
    arguments: Array<Any?>,
): Any? {
    val closedOwner = starProjectionClosedView(receiver.starProjectionRuntimeType(), openGenericType, closedViewHint)
        ?: throw ClassCastException("Value is not an instance of " + openGenericType.fullName)
    // Resolve against the runtime OPEN definition first. Its signature still contains owner type parameters, so the
    // compile-time declaration key remains comparable even when the receiver is G<String>. The chosen open member's
    // runtime token then maps one-to-one to its substituted MethodInfo on the closed receiver view. This also prevents
    // a ref.dll token that happens to identify a DIFFERENT implementation member from being accepted accidentally.
    val openMethod = starProjectionOpenMethod(openGenericType, metadataToken, memberName,
        methodArity, parameterTypeKeys)
    var target: StarProjectionMethod? = null
    for (candidate in closedOwner.getMethods()) {
        if (candidate.metadataToken == openMethod.metadataToken && candidate.module == openMethod.module) {
            target = candidate
            break
        }
    }
    var method = target ?: throw IllegalStateException(
        "Missing star-projection member " + openGenericType.fullName + " token " + metadataToken
    )
    if (methodTypeArguments.size != 0) method = method.makeGenericMethod(methodTypeArguments)
    try {
        return method.invoke(receiver, arguments)
    } catch (failure: StarProjectionInvocationException) {
        throw (failure.innerException ?: failure)
    }
}

// A boxed generic value receiver must be written back even when its method throws. Keep the invoke result separate
// so bir2cir can publish the mutated box before consuming (and possibly rethrowing) the result. Foreign-star
// ref/out and ref-return signatures are refused before this runtime because object[] cannot preserve their aliasing.
@PublishedApi
internal class StarProjectionInvocationOutcome(
    @PublishedApi internal val value: Any?,
    @PublishedApi internal val failure: Throwable?,
)

@PublishedApi
internal fun starProjectionInvokeCaptured(
    receiver: Any,
    openGenericType: StarProjectionType,
    closedViewHint: StarProjectionType?,
    metadataToken: Int,
    memberName: String,
    methodArity: Int,
    parameterTypeKeys: Array<String>,
    methodTypeArguments: Array<StarProjectionType>,
    arguments: Array<Any?>,
): StarProjectionInvocationOutcome = try {
    StarProjectionInvocationOutcome(
        starProjectionInvoke(receiver, openGenericType, closedViewHint, metadataToken, memberName, methodArity,
            parameterTypeKeys, methodTypeArguments, arguments),
        null,
    )
} catch (failure: Throwable) {
    StarProjectionInvocationOutcome(null, failure)
}

@PublishedApi
internal fun starProjectionInvocationValue(outcome: StarProjectionInvocationOutcome): Any? {
    if (outcome.failure != null) throw outcome.failure
    return outcome.value
}

@PublishedApi
internal fun starProjectionInvocationUnit(outcome: StarProjectionInvocationOutcome) {
    if (outcome.failure != null) throw outcome.failure
}

@PublishedApi
internal fun starProjectionInvokeUnit(
    receiver: Any,
    openGenericType: StarProjectionType,
    closedViewHint: StarProjectionType?,
    metadataToken: Int,
    memberName: String,
    methodArity: Int,
    parameterTypeKeys: Array<String>,
    methodTypeArguments: Array<StarProjectionType>,
    arguments: Array<Any?>,
) {
    starProjectionInvoke(receiver, openGenericType, closedViewHint, metadataToken, memberName, methodArity,
        parameterTypeKeys, methodTypeArguments, arguments)
}

@PublishedApi
internal fun starProjectionGetField(
    receiver: Any,
    openGenericType: StarProjectionType,
    closedViewHint: StarProjectionType?,
    metadataToken: Int,
    memberName: String,
): Any? {
    val closedOwner = starProjectionClosedView(receiver.starProjectionRuntimeType(), openGenericType, closedViewHint)
        ?: throw ClassCastException("Value is not an instance of " + openGenericType.fullName)
    val openField = starProjectionOpenField(openGenericType, metadataToken, memberName)
    for (candidate in closedOwner.getFields()) {
        if (candidate.metadataToken == openField.metadataToken && candidate.module == openField.module)
            return candidate.getValue(receiver)
    }
    throw IllegalStateException("Missing star-projection field " + openGenericType.fullName + " token " + metadataToken)
}

@PublishedApi
internal fun starProjectionSetField(
    receiver: Any,
    openGenericType: StarProjectionType,
    closedViewHint: StarProjectionType?,
    metadataToken: Int,
    memberName: String,
    value: Any?,
) {
    val closedOwner = starProjectionClosedView(receiver.starProjectionRuntimeType(), openGenericType, closedViewHint)
        ?: throw ClassCastException("Value is not an instance of " + openGenericType.fullName)
    val openField = starProjectionOpenField(openGenericType, metadataToken, memberName)
    for (candidate in closedOwner.getFields()) {
        if (candidate.metadataToken == openField.metadataToken && candidate.module == openField.module) {
            candidate.setValue(receiver, value)
            return
        }
    }
    throw IllegalStateException("Missing star-projection field " + openGenericType.fullName + " token " + metadataToken)
}

private fun starProjectionOpenMethod(
    openGenericType: StarProjectionType,
    metadataToken: Int,
    memberName: String,
    methodArity: Int,
    parameterTypeKeys: Array<String>,
): StarProjectionMethod {
    var tokenMatch: StarProjectionMethod? = null
    var structuralMatch: StarProjectionMethod? = null
    for (candidate in openGenericType.getMethods()) {
        if (!starProjectionDeclaresOn(candidate.declaringType, openGenericType)
            || candidate.name != memberName
            || candidate.getGenericArguments().size != methodArity) continue
        val parameters = candidate.getParameters()
        if (parameters.size != parameterTypeKeys.size) continue
        var matches = true
        var index = 0
        while (index < parameters.size) {
            if (starProjectionTypeKey(parameters[index].parameterType) != parameterTypeKeys[index]) {
                matches = false
                break
            }
            index++
        }
        if (!matches) continue
        if (candidate.metadataToken == metadataToken && candidate.module == openGenericType.module)
            tokenMatch = candidate
        if (structuralMatch != null) throw IllegalStateException(
            "Ambiguous star-projection member " + openGenericType.fullName + "." + memberName
        )
        structuralMatch = candidate
    }
    return tokenMatch ?: structuralMatch ?: throw IllegalStateException(
        "Missing star-projection member " + openGenericType.fullName + " token " + metadataToken
    )
}

private fun starProjectionOpenField(
    openGenericType: StarProjectionType,
    metadataToken: Int,
    memberName: String,
): StarProjectionField {
    var nameMatch: StarProjectionField? = null
    for (candidate in openGenericType.getFields()) {
        if (!starProjectionDeclaresOn(candidate.declaringType, openGenericType) || candidate.name != memberName)
            continue
        if (candidate.metadataToken == metadataToken && candidate.module == openGenericType.module) return candidate
        if (nameMatch != null) throw IllegalStateException(
            "Ambiguous star-projection field " + openGenericType.fullName + "." + memberName
        )
        nameMatch = candidate
    }
    return nameMatch ?: throw IllegalStateException(
        "Missing star-projection field " + openGenericType.fullName + " token " + metadataToken
    )
}

private fun starProjectionDeclaresOn(
    declaringType: StarProjectionType?,
    openGenericType: StarProjectionType,
): Boolean = declaringType != null && (declaringType == openGenericType
    || declaringType.isGenericType && declaringType.getGenericTypeDefinition() == openGenericType)

private fun starProjectionTypeKey(type: StarProjectionType): String {
    if (type.isGenericParameter)
        return (if (type.declaringMethod == null) "t" else "m") + type.genericParameterPosition
    if (type.isByRef) return "r[" + starProjectionTypeKey(type.getElementType()!!) + "]"
    if (type.isArray)
        return "a" + type.getArrayRank() + "[" + starProjectionTypeKey(type.getElementType()!!) + "]"
    if (type.isGenericType) {
        val definition = if (type.isGenericTypeDefinition) type else type.getGenericTypeDefinition()
        var result = "g{" + definition.fullName + "}<"
        val arguments = type.getGenericArguments()
        var index = 0
        while (index < arguments.size) {
            if (index != 0) result += ","
            result += starProjectionTypeKey(arguments[index])
            index++
        }
        return result + ">"
    }
    return "n{" + type.fullName + "}"
}

private fun starProjectionClosedView(
    runtimeType: StarProjectionType,
    openGenericType: StarProjectionType,
    closedViewHint: StarProjectionType? = null,
): StarProjectionType? {
    // The compiler's exact witness describes the authored receiver.  An inherited member can be declared on a
    // different open generic (`Derived<String>` -> `Base<String>`), so translate that witness through its physical
    // base/interface graph before comparing it with the declaring closure.  Calling this helper without a hint is
    // also the ambiguity check: two distinct closed interface views are never guessed.
    val declaringHint = if (closedViewHint != null
        && closedViewHint != openGenericType
        && (!closedViewHint.isGenericType
            || closedViewHint.getGenericTypeDefinition() != openGenericType))
        starProjectionClosedView(closedViewHint, openGenericType, null)
    else closedViewHint
    var current: StarProjectionType? = runtimeType
    while (current != null) {
        if (current == openGenericType) return current
        if (current.isGenericType && current.getGenericTypeDefinition() == openGenericType) {
            if (declaringHint == null || current == declaringHint) return current
        }
        current = current.baseType
    }
    var match: StarProjectionType? = null
    for (candidate in runtimeType.getInterfaces()) {
        if (!candidate.isGenericType || candidate.getGenericTypeDefinition() != openGenericType) continue
        if (declaringHint != null) {
            if (candidate == declaringHint) return candidate
            continue
        }
        if (match != null) throw IllegalStateException(
            "Ambiguous star-projection view " + openGenericType.fullName
        )
        match = candidate
    }
    return match
}

private fun starProjectionHasView(runtimeType: StarProjectionType, openGenericType: StarProjectionType): Boolean =
    starProjectionFirstView(runtimeType, openGenericType) != null

// MutableIterable is covariant while its mutable BCL collection face is invariant. A widened or star-projected
// receiver therefore cannot name ICollection<T> statically even though the runtime object has one exact closed view.
// Resolve that physical view here and invoke only the fixed ICollection slots; source overload names are irrelevant.
private fun erasedMutableCollectionView(receiver: Any): StarProjectionType {
    var match: StarProjectionType? = null
    for (candidate in receiver.starProjectionRuntimeType().getInterfaces()) {
        if (!candidate.isGenericType
            || candidate.getGenericTypeDefinition().fullName != "System.Collections.Generic.ICollection`1") continue
        if (match != null && match != candidate)
            throw IllegalStateException("Ambiguous mutable collection view")
        match = candidate
    }
    return match ?: throw UnsupportedOperationException("MutableIterable has no mutable CLR collection surface")
}

private fun erasedMutableCollectionMethod(receiver: Any, name: String, parameterCount: Int): StarProjectionMethod {
    var match: StarProjectionMethod? = null
    for (candidate in erasedMutableCollectionView(receiver).getMethods()) {
        if (candidate.name != name || candidate.getParameters().size != parameterCount) continue
        if (match != null) throw IllegalStateException("Ambiguous ICollection member " + name)
        match = candidate
    }
    return match ?: throw IllegalStateException("Missing ICollection member " + name)
}

@PublishedApi
internal fun mutableCollectionRemoveErased(receiver: Any, element: Any?): Boolean = try {
    erasedMutableCollectionMethod(receiver, "Remove", 1).invoke(receiver, arrayOf(element)) as Boolean
} catch (failure: StarProjectionInvocationException) {
    throw (failure.innerException ?: failure)
}

@PublishedApi
internal fun mutableCollectionReplaceErased(receiver: Any, elements: Array<Any?>) {
    val clear = erasedMutableCollectionMethod(receiver, "Clear", 0)
    val add = erasedMutableCollectionMethod(receiver, "Add", 1)
    try {
        clear.invoke(receiver, arrayOfNulls<Any?>(0))
        for (element in elements) add.invoke(receiver, arrayOf(element))
    } catch (failure: StarProjectionInvocationException) {
        throw (failure.innerException ?: failure)
    }
}

private fun erasedMutableIteratorMethod(receiver: Any, name: String): StarProjectionMethod {
    var view: StarProjectionType? = null
    for (candidate in receiver.starProjectionRuntimeType().getInterfaces()) {
        if (!candidate.isGenericType
            || candidate.getGenericTypeDefinition().fullName != "kotlin.collections.MutableIterator`1") continue
        if (view != null && view != candidate) throw IllegalStateException("Ambiguous mutable iterator view")
        view = candidate
    }
    val closed = view ?: throw IllegalStateException("Missing mutable iterator view")
    var match: StarProjectionMethod? = null
    for (candidate in closed.getMethods()) {
        if (candidate.name != name || candidate.getParameters().size != 0) continue
        if (match != null) throw IllegalStateException("Ambiguous MutableIterator member " + name)
        match = candidate
    }
    return match ?: throw IllegalStateException("Missing MutableIterator member " + name)
}

@PublishedApi
internal fun mutableIteratorHasNextErased(receiver: Any): Boolean = try {
    erasedMutableIteratorMethod(receiver, "hasNext").invoke(receiver, arrayOfNulls<Any?>(0)) as Boolean
} catch (failure: StarProjectionInvocationException) {
    throw (failure.innerException ?: failure)
}

@PublishedApi
internal fun mutableIteratorNextErased(receiver: Any): Any? = try {
    erasedMutableIteratorMethod(receiver, "next").invoke(receiver, arrayOfNulls<Any?>(0))
} catch (failure: StarProjectionInvocationException) {
    throw (failure.innerException ?: failure)
}

@PublishedApi
internal fun mutableIteratorRemoveErased(receiver: Any) {
    try {
        erasedMutableIteratorMethod(receiver, "remove").invoke(receiver, arrayOfNulls<Any?>(0))
    } catch (failure: StarProjectionInvocationException) {
        throw (failure.innerException ?: failure)
    }
}

// Classifier checks need existence, not ForeignStarProjectionBinding's unique constructed witness. A CLR type may
// legally implement the same open interface more than once; either closure proves the erased Kotlin classifier.
private fun starProjectionFirstView(
    runtimeType: StarProjectionType,
    openGenericType: StarProjectionType,
): StarProjectionType? {
    var current: StarProjectionType? = runtimeType
    while (current != null) {
        if (current == openGenericType) return current
        if (current.isGenericType && current.getGenericTypeDefinition() == openGenericType) return current
        current = current.baseType
    }
    for (candidate in runtimeType.getInterfaces())
        if (candidate.isGenericType && candidate.getGenericTypeDefinition() == openGenericType) return candidate
    return null
}
