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

@kotlin.clr.ClrTypeAlias("System.Reflection.ConstructorInfo")
@PublishedApi
internal interface StarProjectionConstructor {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "DeclaringType")
    val declaringType: StarProjectionType?

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "MetadataToken")
    val metadataToken: Int

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Module")
    val module: StarProjectionModule

    @kotlin.clr.ClrIntrinsic("GetParameters")
    fun getParameters(): Array<StarProjectionParameter>

    @kotlin.clr.ClrIntrinsic("Invoke")
    fun invoke(arguments: Array<Any?>): Any?
}

@kotlin.clr.ClrTypeAlias("System.Reflection.BindingFlags")
@kotlin.clr.ClrEnum
internal enum class StarProjectionBindingFlags(value: Int) {
    INSTANCE_PUBLIC_NON_PUBLIC(52),
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
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "IsValueType")
    val isValueType: Boolean

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

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "IsSZArray")
    val isSzArray: Boolean

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

    @kotlin.clr.ClrIntrinsic("MakeGenericType")
    fun makeGenericType(typeArguments: Array<StarProjectionType>): StarProjectionType

    @kotlin.clr.ClrIntrinsic("GetElementType")
    fun getElementType(): StarProjectionType?

    @kotlin.clr.ClrIntrinsic("GetArrayRank")
    fun getArrayRank(): Int

    @kotlin.clr.ClrIntrinsic("GetMethods")
    fun getMethods(): Array<StarProjectionMethod>

    @kotlin.clr.ClrIntrinsic("GetConstructors")
    fun getConstructors(flags: StarProjectionBindingFlags): Array<StarProjectionConstructor>

    @kotlin.clr.ClrIntrinsic("GetFields")
    fun getFields(): Array<StarProjectionField>

    @kotlin.clr.ClrIntrinsic("GetInterfaces")
    fun getInterfaces(): Array<StarProjectionType>
}

@PublishedApi
internal fun starProjectionTypeAcceptsNull(type: StarProjectionType): Boolean =
    !type.isValueType || (type.isGenericType && type.getGenericTypeDefinition().fullName == "System.Nullable`1")

@kotlin.clr.ClrTypeAlias("System.Reflection.TargetInvocationException")
@PublishedApi
internal open class StarProjectionInvocationException : Throwable() {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "InnerException")
    val innerException: Throwable? get() = null
}

@kotlin.clr.ClrTypeAlias("System.Delegate")
@PublishedApi
internal interface StarProjectionDelegate {
    @kotlin.clr.ClrIntrinsic("DynamicInvoke")
    fun dynamicInvoke(arguments: Array<Any?>): Any?
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

// String, arrays and dictionary storage have CLR enumerators without Kotlin Iterable membership.
// Declaration-owned Kotlin identities are checked before this foreign-storage policy, so a map that
// explicitly implements Kotlin Iterable still has its declared identity.
private fun foreignKotlinIterable(value: Any?, dictionary: StarProjectionType, readOnlyDictionary: StarProjectionType,
    set: StarProjectionType, readOnlySet: StarProjectionType, list: StarProjectionType,
    genericList: StarProjectionType, readOnlyList: StarProjectionType): Boolean {
    if (value == null) return true
    if (value !is kotlin.collections.ClrRawEnumerable || value is String) return false
    val type = value.starProjectionRuntimeType()
    if (type.isArray) return false
    // Independent List/Set contracts still grant iteration when the object also implements a dictionary.
    if (starProjectionHasView(type, list) || starProjectionHasView(type, genericList)
        || starProjectionHasView(type, readOnlyList) || starProjectionHasView(type, set)
        || starProjectionHasView(type, readOnlySet)) return true
    return value !is kotlin.collections.ClrRawDictionary
        && !starProjectionHasView(type, dictionary) && !starProjectionHasView(type, readOnlyDictionary)
}

// bir2cir supplies a trusted Kotlin map-only class alias's exact storage definition. Interfaces
// inherited from that storage remain operational faces, while a foreign subtype can introduce a
// genuinely additional closed List/Set contract. Element-type guesses cannot make this distinction.
@PublishedApi
internal fun kotlinCollectionStorageMatches(value: Any?, witness: Int, storage: StarProjectionType,
    list: StarProjectionType, genericList: StarProjectionType, readOnlyList: StarProjectionType,
    set: StarProjectionType, readOnlySet: StarProjectionType): Boolean {
    if (value == null || value is KotlinIterableClassifier) return true
    when (witness and -2) {
        2, 4, 6, 8, 14, 16 -> {}
        else -> return true
    }
    val type = value.starProjectionRuntimeType()
    val storageView = starProjectionClosedView(type, storage) ?: return true
    if (type == storageView) return false
    val storageFaces = storageView.getInterfaces()
    for (face in type.getInterfaces()) {
        val definition = if (face.isGenericType) face.getGenericTypeDefinition() else face
        val mutableList = definition == list || definition == genericList
        val readableList = mutableList || definition == readOnlyList
        val eligible = when (witness and -2) {
            6 -> readableList
            8 -> mutableList
            4 -> mutableList || definition == set
            else -> readableList || definition == set || definition == readOnlySet
        }
        if (!eligible) continue
        var inherited = false
        for (storageFace in storageFaces) {
            if (face == storageFace) { inherited = true; break }
        }
        if (!inherited) return true
    }
    return false
}

@PublishedApi
internal fun kotlinCollectionStorageCastCandidate(value: Any?, witness: Int, storage: StarProjectionType,
    list: StarProjectionType, genericList: StarProjectionType, readOnlyList: StarProjectionType,
    set: StarProjectionType, readOnlySet: StarProjectionType): Any? {
    if (!kotlinCollectionStorageMatches(value, witness, storage, list, genericList, readOnlyList, set, readOnlySet))
        throw ClassCastException("Value is not an instance of the requested Kotlin collection classifier")
    return value
}

// Operational storage faces do not grant Kotlin mutability. This predicate is separate from the physical type
// test: an erased reified target may be object, which would accept even an ineligible sentinel object.
@PublishedApi
internal fun kotlinCollectionMatches(value: Any?, witness: Int, dictionary: StarProjectionType,
    readOnlyDictionary: StarProjectionType, set: StarProjectionType, readOnlySet: StarProjectionType,
    list: StarProjectionType, genericList: StarProjectionType, readOnlyList: StarProjectionType,
    collection: StarProjectionType, readOnlyCollection: StarProjectionType): Boolean {
    if (value !is KotlinIterableClassifier) return when (witness and -2) {
        14, 16 -> foreignKotlinIterable(value, dictionary, readOnlyDictionary, set, readOnlySet,
            list, genericList, readOnlyList)
        // A reified star target may have become object by the physical test. The witness must
        // retain the actual family, not merely iteration eligibility or an erased object test.
        2, 4 -> value == null || (foreignKotlinIterable(value, dictionary, readOnlyDictionary, set, readOnlySet,
            list, genericList, readOnlyList) && starProjectionKotlinCollectionIsInstance(value,
                if ((witness and -2) == 2) 0 else 5,
                if ((witness and -2) == 2) readOnlyCollection else collection, collection))
        10, 12 -> value == null || starProjectionKotlinCollectionIsInstance(value,
            if ((witness and -2) == 10) 1 else 2,
            if ((witness and -2) == 10) readOnlySet else set, set)
        6, 8 -> value == null || (foreignKotlinIterable(value, dictionary, readOnlyDictionary, set, readOnlySet,
            list, genericList, readOnlyList) && starProjectionKotlinCollectionIsInstance(value,
                if ((witness and -2) == 6) 3 else 4,
                if ((witness and -2) == 6) readOnlyList else genericList, genericList))
        else -> true
    }
    // bir2cir supplies KotlinTypeWitness: low bit is nullability; the remaining code is nominal identity.
    return when (witness and -2) {
        2 -> value is KotlinCollectionClassifier
        4 -> value is KotlinMutableCollectionClassifier
        6 -> value is KotlinListClassifier
        8 -> value is KotlinMutableListClassifier
        10 -> value is KotlinSetClassifier
        12 -> value is KotlinMutableSetClassifier
        14 -> true
        16 -> value is KotlinMutableIterableClassifier
        else -> true
    }
}

@PublishedApi
internal fun kotlinCollectionCastCandidate(value: Any?, witness: Int, dictionary: StarProjectionType,
    readOnlyDictionary: StarProjectionType, set: StarProjectionType, readOnlySet: StarProjectionType,
    list: StarProjectionType, genericList: StarProjectionType, readOnlyList: StarProjectionType,
    collection: StarProjectionType, readOnlyCollection: StarProjectionType): Any? {
    if (!kotlinCollectionMatches(value, witness, dictionary, readOnlyDictionary, set, readOnlySet,
            list, genericList, readOnlyList, collection, readOnlyCollection))
        throw ClassCastException("Value is not an instance of the requested Kotlin collection classifier")
    return value
}

@kotlin.clr.ClrTypeAlias("System.Collections.IList")
private interface StarProjectionRawList {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Count") val count: Int
    @kotlin.clr.ClrIntrinsic("get_Item") fun get(index: Int): Any?
}

@kotlin.clr.ClrTypeAlias("System.Collections.ICollection")
private interface StarProjectionRawCollection {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Count") val count: Int
}

// Collection/Set/List use overlapping BCL faces for member dispatch, so their Kotlin classifier is a composite physical
// fact. KotlinCollectionClassifierLowering supplies the eligibility guard before this physical test,
// including on the cast operand consumed by smart-cast member lowering. Do not repeat a dictionary
// exclusion here: a dictionary can have an independent List/Set contract that grants Collection.
// Emitted Kotlin identities and the actual generic CLR faces select the narrower physical classifier.
@PublishedApi
internal fun starProjectionKotlinCollectionIsInstance(
    value: Any?,
    kind: Int,
    firstOpenType: StarProjectionType,
    secondOpenType: StarProjectionType,
): Boolean {
    if (value == null) return false
    if (kind == 2 && value is KotlinMutableSetClassifier) return true
    if (kind == 1 && value is KotlinSetClassifier) return true
    if (kind == 0 && value is KotlinCollectionClassifier) return true
    if (kind == 3 && value is KotlinListClassifier) return true
    if (kind == 4 && value is KotlinMutableListClassifier) return true
    if (kind == 5 && value is KotlinMutableCollectionClassifier) return true
    if ((kind == 0 || kind == 5) && value is StarProjectionRawCollection) return true
    if ((kind == 3 || kind == 4) && value is StarProjectionRawList) return true
    val runtimeType = value.starProjectionRuntimeType()
    return starProjectionHasView(runtimeType, firstOpenType) || starProjectionHasView(runtimeType, secondOpenType)
}

@PublishedApi
internal fun starProjectionKotlinNullableCollectionIsInstance(
    value: Any?,
    kind: Int,
    firstOpenType: StarProjectionType,
    secondOpenType: StarProjectionType,
): Boolean = value == null || starProjectionKotlinCollectionIsInstance(value, kind, firstOpenType, secondOpenType)

@PublishedApi
internal fun starProjectionKotlinCollectionCast(
    value: Any?,
    kind: Int,
    firstOpenType: StarProjectionType,
    secondOpenType: StarProjectionType,
): Any {
    if (starProjectionKotlinCollectionIsInstance(value, kind, firstOpenType, secondOpenType)) return value!!
    throw ClassCastException("Value is not an instance of the requested Kotlin collection classifier")
}

@PublishedApi
internal fun starProjectionKotlinCollectionSafeCast(
    value: Any?,
    kind: Int,
    firstOpenType: StarProjectionType,
    secondOpenType: StarProjectionType,
): Any? = if (starProjectionKotlinCollectionIsInstance(value, kind, firstOpenType, secondOpenType)) value else null

@PublishedApi
internal fun starProjectionKotlinNullableCollectionCast(
    value: Any?,
    kind: Int,
    firstOpenType: StarProjectionType,
    secondOpenType: StarProjectionType,
): Any? = if (value == null) null else starProjectionKotlinCollectionCast(value, kind, firstOpenType, secondOpenType)

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

// A member result can itself be a delegate whose closed generic signature contains the hidden owner argument.
// bir2cir wraps it in a statically typed Kotlin-facing closure; only this boundary sees the opaque CLR delegate.
@PublishedApi
internal fun starProjectionInvokeDelegate(receiver: Any?, arguments: Array<Any?>): Any? = try {
    (receiver as StarProjectionDelegate).dynamicInvoke(arguments)
} catch (failure: StarProjectionInvocationException) {
    throw (failure.innerException ?: failure)
}

// CLR has no syntax for constructing G<Capture> when the capture arrives through a Kotlin G<*> value. The compiler
// supplies the exact open constructor descriptor and fallback bounds; this helper recovers only the hidden closed
// owner arguments from the runtime generic witnesses carried by the constructor arguments, closes that exact owner,
// and invokes the already-selected constructor. It never selects an overload from argument values.
@PublishedApi
internal fun starProjectionConstruct(
    openGenericType: StarProjectionType,
    parameterTypeKeys: Array<String>,
    fallbackTypeArguments: Array<StarProjectionType>,
    arguments: Array<Any?>,
): Any? {
    val openConstructor = starProjectionOpenConstructor(openGenericType, parameterTypeKeys)
    val parameters = openConstructor.getParameters()
    if (parameters.size != arguments.size)
        throw IllegalStateException("Projected constructor argument count changed")
    val ownerParameters = openGenericType.getGenericArguments()
    if (ownerParameters.size != fallbackTypeArguments.size)
        throw IllegalStateException("Projected constructor owner arity changed")
    val inferred = arrayOfNulls<StarProjectionType>(ownerParameters.size)
    var index = 0
    while (index < parameters.size) {
        val argument = arguments[index]
        if (argument != null)
            starProjectionBindConstructionSlots(parameters[index].parameterType,
                argument.starProjectionRuntimeType(), inferred)
        index++
    }
    val closedArguments = Array<StarProjectionType>(ownerParameters.size) { slot ->
        inferred[slot] ?: fallbackTypeArguments[slot]
    }
    val closedOwner = openGenericType.makeGenericType(closedArguments)
    var closedConstructor: StarProjectionConstructor? = null
    for (candidate in closedOwner.getConstructors(StarProjectionBindingFlags.INSTANCE_PUBLIC_NON_PUBLIC)) {
        if (candidate.metadataToken == openConstructor.metadataToken
            && candidate.module == openConstructor.module) {
            if (closedConstructor != null)
                throw IllegalStateException("Ambiguous closed projected constructor")
            closedConstructor = candidate
        }
    }
    val target = closedConstructor ?: throw IllegalStateException("Missing closed projected constructor")
    try {
        return target.invoke(arguments)
    } catch (failure: StarProjectionInvocationException) {
        throw (failure.innerException ?: failure)
    }
}

private fun starProjectionOpenConstructor(
    openGenericType: StarProjectionType,
    parameterTypeKeys: Array<String>,
): StarProjectionConstructor {
    var match: StarProjectionConstructor? = null
    for (candidate in openGenericType.getConstructors(StarProjectionBindingFlags.INSTANCE_PUBLIC_NON_PUBLIC)) {
        if (!starProjectionDeclaresOn(candidate.declaringType, openGenericType)) continue
        val parameters = candidate.getParameters()
        if (parameters.size != parameterTypeKeys.size) continue
        var matches = true
        var index = 0
        while (index < parameters.size) {
            if (starProjectionProjectedConstructorTypeKey(parameters[index].parameterType)
                != parameterTypeKeys[index]) {
                matches = false
                break
            }
            index++
        }
        if (!matches) continue
        if (match != null) throw IllegalStateException(
            "Ambiguous projected constructor " + openGenericType.fullName
        )
        match = candidate
    }
    return match ?: throw IllegalStateException("Missing projected constructor " + openGenericType.fullName)
}

private fun starProjectionBindConstructionSlots(
    declaration: StarProjectionType,
    actual: StarProjectionType,
    bindings: Array<StarProjectionType?>,
) {
    if (declaration.isGenericParameter && declaration.declaringMethod == null) {
        val slot = declaration.genericParameterPosition
        val previous = bindings[slot]
        if (previous != null && previous != actual)
            throw IllegalStateException("Conflicting projected constructor type argument")
        bindings[slot] = actual
        return
    }
    if (declaration.isArray) {
        if (actual.isArray && declaration.isSzArray == actual.isSzArray
            && declaration.getArrayRank() == actual.getArrayRank())
            starProjectionBindConstructionSlots(declaration.getElementType()!!, actual.getElementType()!!, bindings)
        return
    }
    if (!declaration.isGenericType) return
    val openDeclaration = if (declaration.isGenericTypeDefinition) declaration
        else declaration.getGenericTypeDefinition()
    val actualView = starProjectionClosedView(actual, openDeclaration) ?: return
    val declarationArguments = declaration.getGenericArguments()
    val actualArguments = actualView.getGenericArguments()
    if (declarationArguments.size != actualArguments.size) return
    var index = 0
    while (index < declarationArguments.size) {
        starProjectionBindConstructionSlots(declarationArguments[index], actualArguments[index], bindings)
        index++
    }
}

private fun starProjectionProjectedConstructorTypeKey(type: StarProjectionType): String {
    if (type.isGenericParameter)
        return (if (type.declaringMethod == null) "t" else "m") + type.genericParameterPosition
    if (type.isByRef) return "r[" + starProjectionProjectedConstructorTypeKey(type.getElementType()!!) + "]"
    if (type.isArray)
        return "a" + (if (type.isSzArray) "s" else "m") + type.getArrayRank() + "[" +
            starProjectionProjectedConstructorTypeKey(type.getElementType()!!) + "]"
    if (type.isGenericType) {
        val definition = if (type.isGenericTypeDefinition) type else type.getGenericTypeDefinition()
        var result = "g{" + starProjectionNormalizedTypeName(definition.fullName) + "}<"
        val arguments = type.getGenericArguments()
        var index = 0
        while (index < arguments.size) {
            if (index != 0) result += ","
            result += starProjectionProjectedConstructorTypeKey(arguments[index])
            index++
        }
        return result + ">"
    }
    return "n{" + starProjectionNormalizedTypeName(type.fullName) + "}"
}

private fun starProjectionNormalizedTypeName(name: String?): String {
    if (name == null) return ""
    var result = ""
    var index = 0
    while (index < name.length) {
        val c = name[index]
        if (c == '`') {
            index++
            while (index < name.length && name[index] >= '0' && name[index] <= '9') index++
        } else {
            result += if (c == '+') '.' else c
            index++
        }
    }
    return result
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
        return "a" + (if (type.isSzArray) "s" else "m") + type.getArrayRank() + "[" +
            starProjectionTypeKey(type.getElementType()!!) + "]"
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

private fun erasedMutableListView(receiver: Any): StarProjectionType {
    var match: StarProjectionType? = null
    for (candidate in receiver.starProjectionRuntimeType().getInterfaces()) {
        if (!candidate.isGenericType
            || candidate.getGenericTypeDefinition().fullName != "System.Collections.Generic.IList`1") continue
        if (match != null && match != candidate)
            throw IllegalStateException("Ambiguous mutable list view")
        match = candidate
    }
    return match ?: throw UnsupportedOperationException("MutableList has no mutable CLR list surface")
}

private fun erasedMutableListMethod(receiver: Any, name: String, parameterCount: Int): StarProjectionMethod {
    var match: StarProjectionMethod? = null
    for (candidate in erasedMutableListView(receiver).getMethods()) {
        if (candidate.name != name || candidate.getParameters().size != parameterCount) continue
        if (match != null) throw IllegalStateException("Ambiguous IList member " + name)
        match = candidate
    }
    return match ?: throw IllegalStateException("Missing IList member " + name)
}

// bir2cir supplies trusted map-only class-alias storage definitions, exactly as for collection classifiers.
// Cache those physical type tokens; do not infer Kotlin storage meaning from runtime CLR class names.
@kotlin.clr.ClrIntrinsic("dotkt.collectionMapStorageTypes")
@PublishedApi
internal fun collectionMapStorageTypes(): Array<StarProjectionType> =
    throw UnsupportedOperationException("compiler intrinsic")

private val collectionMapStorageDefinitions = collectionMapStorageTypes()

private fun collectionMapStorageFaces(type: StarProjectionType): Array<StarProjectionType> {
    var faces = emptyArray<StarProjectionType>()
    for (storage in collectionMapStorageDefinitions) {
        val view = starProjectionClosedView(type, storage) ?: continue
        faces += view.getInterfaces()
    }
    return faces
}

private fun hasExactFace(faces: Array<StarProjectionType>, face: StarProjectionType): Boolean {
    for (candidate in faces) if (candidate == face) return true
    return false
}

private fun rawListIsMapStorage(receiver: Any): Boolean {
    for (face in collectionMapStorageFaces(receiver.starProjectionRuntimeType())) {
        if (face.fullName == "System.Collections.IList") return true
    }
    return false
}

// Prefer the read-only contract and fall back to its mutable counterpart. A List's parent Collection
// closure can be independent even when the same object also exposes dictionary entry storage.
private fun findErasedProjectedView(receiver: Any, preferred: String, fallback: String,
    excludeDictionaryStorage: Boolean = false): StarProjectionType? {
    val runtimeType = receiver.starProjectionRuntimeType()
    val interfaces = runtimeType.getInterfaces()
    val listView = preferred == "System.Collections.Generic.IReadOnlyList`1"
    val storageFaces = if (excludeDictionaryStorage || listView) collectionMapStorageFaces(runtimeType)
        else emptyArray<StarProjectionType>()
    var preferredMatch: StarProjectionType? = null
    var fallbackMatch: StarProjectionType? = null
    for (candidate in interfaces) {
        if (!candidate.isGenericType) continue
        val definition = candidate.getGenericTypeDefinition().fullName
        if (definition != preferred && definition != fallback) continue
        if (listView && hasExactFace(storageFaces, candidate)) continue
        if (excludeDictionaryStorage) {
            var dictionaryParent = hasExactFace(storageFaces, candidate)
            var independentParent = false
            for (root in interfaces) {
                if (!root.isGenericType) continue
                val rootName = root.getGenericTypeDefinition().fullName
                val dictionary = rootName == "System.Collections.Generic.IDictionary`2"
                    || rootName == "System.Collections.Generic.IReadOnlyDictionary`2"
                val independent = isIndependentCollectionView(root) && !hasExactFace(storageFaces, root)
                if (!dictionary && !independent) continue
                for (parent in root.getInterfaces()) {
                    if (parent == candidate) {
                        if (dictionary) dictionaryParent = true
                        if (independent) independentParent = true
                    }
                }
            }
            if (dictionaryParent && !independentParent) continue
        }
        if (definition == preferred) {
            if (preferredMatch != null && preferredMatch != candidate)
                throw IllegalStateException("Ambiguous projected view " + preferred)
            preferredMatch = candidate
        } else if (definition == fallback) {
            if (fallbackMatch != null && fallbackMatch != candidate)
                throw IllegalStateException("Ambiguous projected view " + fallback)
            fallbackMatch = candidate
        }
    }
    // Preference chooses between two representations of the same element closure, not between
    // unrelated contracts. Distinct element types remain ambiguous across the two definitions too.
    if (preferredMatch != null && fallbackMatch != null
        && preferredMatch.getGenericArguments()[0] != fallbackMatch.getGenericArguments()[0])
        throw IllegalStateException("Ambiguous projected view " + preferred + " or " + fallback)
    return preferredMatch ?: fallbackMatch
}

// List/Set contribute Kotlin Collection contracts independently of dictionary storage. Select their
// actual closed parent interfaces, not a guessed element type or the first reflection result.
private fun isIndependentCollectionView(view: StarProjectionType): Boolean {
    if (!view.isGenericType) return false
    return when (view.getGenericTypeDefinition().fullName) {
        "System.Collections.Generic.IReadOnlyList`1", "System.Collections.Generic.IList`1",
        "System.Collections.Generic.IReadOnlySet`1", "System.Collections.Generic.ISet`1" -> true
        else -> false
    }
}

private fun erasedProjectedMethod(view: StarProjectionType, name: String, parameterCount: Int): StarProjectionMethod {
    var match: StarProjectionMethod? = null
    for (candidate in view.getMethods()) {
        if (candidate.name != name || candidate.getParameters().size != parameterCount) continue
        if (match != null) throw IllegalStateException("Ambiguous projected member " + name)
        match = candidate
    }
    return match ?: throw IllegalStateException("Missing projected member " + name)
}

@PublishedApi
internal fun projectedCollectionCountErased(receiver: Any): Int {
    val preferred = "System.Collections.Generic.IReadOnlyCollection`1"
    val fallback = "System.Collections.Generic.ICollection`1"
    val view = findErasedProjectedView(receiver, preferred, fallback, excludeDictionaryStorage = true)
    // Preserve an existing generic view (and ambiguity errors). Raw Count supplies a capability only when
    // neither generic collection face exists. Native getter exceptions must not be unwrapped as reflection failures.
    if (view == null && receiver is StarProjectionRawCollection) return receiver.count
    val selected = view
        ?: throw UnsupportedOperationException("Projected receiver has no " + preferred + " or " + fallback + " surface")
    return try {
        erasedProjectedMethod(selected, "get_Count", 0).invoke(receiver, arrayOfNulls<Any?>(0)) as Int
    } catch (failure: StarProjectionInvocationException) {
        throw (failure.innerException ?: failure)
    }
}

@PublishedApi
internal fun projectedListCountErased(receiver: Any): Int {
    val view = findErasedProjectedView(receiver,
        "System.Collections.Generic.IReadOnlyList`1", "System.Collections.Generic.IList`1")
    if (view == null && receiver is StarProjectionRawList && !rawListIsMapStorage(receiver)) return receiver.count
    val list = view ?: throw UnsupportedOperationException("Projected receiver has no CLR List surface")
    val collectionName = if (list.getGenericTypeDefinition().fullName == "System.Collections.Generic.IReadOnlyList`1")
        "System.Collections.Generic.IReadOnlyCollection`1" else "System.Collections.Generic.ICollection`1"
    return projectedParentCollectionCount(receiver, list, collectionName)
}

@PublishedApi
internal fun projectedSetCountErased(receiver: Any): Int {
    val set = findErasedProjectedView(receiver,
        "System.Collections.Generic.IReadOnlySet`1", "System.Collections.Generic.ISet`1")
        ?: throw UnsupportedOperationException("Projected receiver has no CLR Set surface")
    val collectionName = if (set.getGenericTypeDefinition().fullName == "System.Collections.Generic.IReadOnlySet`1")
        "System.Collections.Generic.IReadOnlyCollection`1" else "System.Collections.Generic.ICollection`1"
    return projectedParentCollectionCount(receiver, set, collectionName)
}

@PublishedApi
internal fun projectedMutableListCountErased(receiver: Any): Int {
    val view = findErasedProjectedView(receiver,
        "System.Collections.Generic.IList`1", "System.Collections.Generic.IList`1")
    if (view == null && receiver is StarProjectionRawList && !rawListIsMapStorage(receiver)) return receiver.count
    val list = view ?: throw UnsupportedOperationException("Projected receiver has no mutable CLR List surface")
    return projectedParentCollectionCount(receiver, list, "System.Collections.Generic.ICollection`1")
}

@PublishedApi
internal fun projectedMutableSetCountErased(receiver: Any): Int {
    val set = findErasedProjectedView(receiver,
        "System.Collections.Generic.ISet`1", "System.Collections.Generic.ISet`1")
        ?: throw UnsupportedOperationException("Projected receiver has no mutable CLR Set surface")
    return projectedParentCollectionCount(receiver, set, "System.Collections.Generic.ICollection`1")
}

// Follow the selected family to its actual parent Count declaration. Unrelated Collection
// implementations on the original receiver cannot change that family's size.
private fun projectedParentCollectionCount(receiver: Any, view: StarProjectionType, collectionName: String): Int {
    return try {
        var getter: StarProjectionMethod? = null
        for (parent in view.getInterfaces()) {
            if (!parent.isGenericType || parent.getGenericTypeDefinition().fullName != collectionName) continue
            for (method in parent.getMethods()) {
                if (method.name != "get_Count" || method.getParameters().size != 0) continue
                if (getter != null) throw IllegalStateException("Ambiguous parent Count slot")
                getter = method
            }
        }
        val selected = getter ?: throw IllegalStateException("Missing parent Count slot")
        selected.invoke(receiver, arrayOfNulls<Any?>(0)) as Int
    } catch (failure: StarProjectionInvocationException) {
        throw (failure.innerException ?: failure)
    }
}

@PublishedApi
internal fun projectedListGetErased(receiver: Any, index: Int): Any? {
    val view = findErasedProjectedView(receiver,
        "System.Collections.Generic.IReadOnlyList`1", "System.Collections.Generic.IList`1")
    if (view == null && receiver is StarProjectionRawList && !rawListIsMapStorage(receiver)) return receiver.get(index)
    val list = view ?: throw UnsupportedOperationException("Projected receiver has no CLR List surface")
    return try {
        erasedProjectedMethod(list, "get_Item", 1).invoke(receiver, arrayOf(index))
    } catch (failure: StarProjectionInvocationException) {
        throw (failure.innerException ?: failure)
    }
}

@PublishedApi
internal fun projectedReadOnlyCollectionCountErased(receiver: Any): Int =
    projectedCollectionCountErased(receiver)

@PublishedApi
internal fun projectedMutableCollectionCountErased(receiver: Any): Int {
    val name = "System.Collections.Generic.ICollection`1"
    val view = findErasedProjectedView(receiver, name, name, excludeDictionaryStorage = true)
    if (view == null && receiver is StarProjectionRawCollection) return receiver.count
    val selected = view ?: throw UnsupportedOperationException("Projected receiver has no mutable CLR Collection surface")
    return try {
        erasedProjectedMethod(selected, "get_Count", 0).invoke(receiver, arrayOfNulls<Any?>(0)) as Int
    } catch (failure: StarProjectionInvocationException) {
        throw (failure.innerException ?: failure)
    }
}

@PublishedApi
internal fun mutableCollectionCountErased(receiver: Any): Int = try {
    erasedMutableCollectionMethod(receiver, "get_Count", 0)
        .invoke(receiver, arrayOfNulls<Any?>(0)) as Int
} catch (failure: StarProjectionInvocationException) {
    throw (failure.innerException ?: failure)
}

@PublishedApi
internal fun mutableCollectionAddErased(receiver: Any, element: Any?): Boolean = try {
    val count = erasedMutableCollectionMethod(receiver, "get_Count", 0)
    val add = erasedMutableCollectionMethod(receiver, "Add", 1)
    val before = count.invoke(receiver, arrayOfNulls<Any?>(0)) as Int
    add.invoke(receiver, arrayOf(element))
    (count.invoke(receiver, arrayOfNulls<Any?>(0)) as Int) != before
} catch (failure: StarProjectionInvocationException) {
    throw (failure.innerException ?: failure)
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

@PublishedApi
internal fun mutableListGetErased(receiver: Any, index: Int): Any? = try {
    erasedMutableListMethod(receiver, "get_Item", 1).invoke(receiver, arrayOf(index))
} catch (failure: StarProjectionInvocationException) {
    throw (failure.innerException ?: failure)
}

@PublishedApi
internal fun mutableListSetErased(receiver: Any, index: Int, element: Any?) {
    try {
        erasedMutableListMethod(receiver, "set_Item", 2).invoke(receiver, arrayOf(index, element))
    } catch (failure: StarProjectionInvocationException) {
        throw (failure.innerException ?: failure)
    }
}

@PublishedApi
internal fun mutableListInsertErased(receiver: Any, index: Int, element: Any?) {
    try {
        erasedMutableListMethod(receiver, "Insert", 2).invoke(receiver, arrayOf(index, element))
    } catch (failure: StarProjectionInvocationException) {
        throw (failure.innerException ?: failure)
    }
}

@PublishedApi
internal fun mutableListRemoveAtErased(receiver: Any, index: Int) {
    try {
        erasedMutableListMethod(receiver, "RemoveAt", 1).invoke(receiver, arrayOf(index))
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

private fun erasedIteratorMethod(receiver: Any, openInterface: String, name: String, parameterCount: Int): StarProjectionMethod {
    var view: StarProjectionType? = null
    for (candidate in receiver.starProjectionRuntimeType().getInterfaces()) {
        if (!candidate.isGenericType || candidate.getGenericTypeDefinition().fullName != openInterface) continue
        if (view != null && view != candidate) throw IllegalStateException("Ambiguous iterator view " + openInterface)
        view = candidate
    }
    val closed = view ?: throw IllegalStateException("Missing iterator view " + openInterface)
    var match: StarProjectionMethod? = null
    for (candidate in closed.getMethods()) {
        if (candidate.name != name || candidate.getParameters().size != parameterCount) continue
        if (match != null) throw IllegalStateException("Ambiguous iterator member " + name)
        match = candidate
    }
    return match ?: throw IllegalStateException("Missing iterator member " + name)
}

private fun invokeIteratorErased(receiver: Any, openInterface: String, name: String, arguments: Array<Any?>): Any? = try {
    erasedIteratorMethod(receiver, openInterface, name, arguments.size).invoke(receiver, arguments)
} catch (failure: StarProjectionInvocationException) {
    throw (failure.innerException ?: failure)
}

@PublishedApi
internal fun listIteratorHasNextErased(receiver: Any): Boolean =
    invokeIteratorErased(receiver, "kotlin.collections.Iterator`1", "hasNext", arrayOfNulls<Any?>(0)) as Boolean

@PublishedApi
internal fun listIteratorNextErased(receiver: Any): Any? =
    invokeIteratorErased(receiver, "kotlin.collections.Iterator`1", "next", arrayOfNulls<Any?>(0))

@PublishedApi
internal fun listIteratorHasPreviousErased(receiver: Any): Boolean =
    invokeIteratorErased(receiver, "kotlin.collections.ListIterator`1", "hasPrevious", arrayOfNulls<Any?>(0)) as Boolean

@PublishedApi
internal fun listIteratorPreviousErased(receiver: Any): Any? =
    invokeIteratorErased(receiver, "kotlin.collections.ListIterator`1", "previous", arrayOfNulls<Any?>(0))

@PublishedApi
internal fun listIteratorNextIndexErased(receiver: Any): Int =
    invokeIteratorErased(receiver, "kotlin.collections.ListIterator`1", "nextIndex", arrayOfNulls<Any?>(0)) as Int

@PublishedApi
internal fun listIteratorPreviousIndexErased(receiver: Any): Int =
    invokeIteratorErased(receiver, "kotlin.collections.ListIterator`1", "previousIndex", arrayOfNulls<Any?>(0)) as Int

@PublishedApi
internal fun mutableListIteratorSetErased(receiver: Any, element: Any?) {
    invokeIteratorErased(receiver, "kotlin.collections.MutableListIterator`1", "set", arrayOf(element))
}

@PublishedApi
internal fun mutableListIteratorAddErased(receiver: Any, element: Any?) {
    invokeIteratorErased(receiver, "kotlin.collections.MutableListIterator`1", "add", arrayOf(element))
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
        if (candidate == openGenericType
            || candidate.isGenericType && candidate.getGenericTypeDefinition() == openGenericType) return candidate
    return null
}
