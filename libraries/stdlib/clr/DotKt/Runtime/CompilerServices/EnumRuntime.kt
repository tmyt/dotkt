package DotKt.Runtime.CompilerServices

// Compiler-owned comparison face for singleton-class enums. Keep it distinct from user-implemented CLR
// IComparable slots: Kotlin's final enum comparison always uses declaration order.
@PublishedApi
internal interface KotlinEnumComparison {
    fun compareEnumTo(other: Any): Int
}

@PublishedApi
internal abstract class KotlinEnumComparable : Comparable<Any> {
    override fun compareTo(other: Any): Int = (this as KotlinEnumComparison).compareEnumTo(other)
}

@kotlin.clr.ClrTypeAlias("System.Enum")
private interface NativeEnumValue

@kotlin.clr.ClrTypeAlias("System.Array")
private interface EnumArrayStorage

@PublishedApi
internal fun enumMatches(value: Any?, arrayShape: String): Boolean {
    if (arrayShape.length == 0) return value is NativeEnumValue || value is KotlinEnumComparison
    return value != null && enumArrayTypeMatches(value.starProjectionRuntimeType(), arrayShape, 0)
}

private fun enumArrayTypeMatches(type: StarProjectionType, shape: String, index: Int): Boolean {
    if (!type.isSzArray) return false
    val component = type.getElementType()!!
    if (index + 1 < shape.length) {
        if (shape[index + 1] == 'o' && component == (EnumArrayStorage::class as StarProjectionType)) return true
        return enumArrayTypeMatches(component, shape, index + 1)
    }
    return component == (Any::class as StarProjectionType) || enumComponentMatches(component)
}

@PublishedApi
internal fun enumComponentMatches(component: StarProjectionType): Boolean =
    (NativeEnumValue::class as StarProjectionType).isAssignableFrom(component) ||
        (KotlinEnumComparison::class as StarProjectionType).isAssignableFrom(component)

@PublishedApi
internal fun enumClassifier(arrayShape: String): (Any?) -> Boolean = { enumMatches(it, arrayShape) }

// Generic `T : Enum<T>` code cannot bake in one enum declaration's ordinal map. Explicit Kotlin CLR enums therefore
// stamp each literal with its declaration ordinal; this runtime reads that exact compiler carrier. Enums without the
// carrier use the same visible FieldDef order that dll2klib projects. Identical native alias bits select the first
// matching declaration; they cannot acquire distinguishable Kotlin entry identity without changing the CLR ABI.

@kotlin.clr.ClrTypeAlias("System.Collections.Generic.IList")
private interface EnumRuntimeList<T> {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Count")
    val count: Int

    @kotlin.clr.ClrIntrinsic("get_Item")
    operator fun get(index: Int): T
}

@kotlin.clr.ClrTypeAlias("System.Reflection.CustomAttributeTypedArgument")
private interface EnumRuntimeAttributeArgument {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Value")
    val value: Any?
}

@kotlin.clr.ClrTypeAlias("System.Reflection.CustomAttributeData")
private interface EnumRuntimeAttribute {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "AttributeType")
    val attributeType: EnumRuntimeType

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "ConstructorArguments")
    val constructorArguments: EnumRuntimeList<EnumRuntimeAttributeArgument>
}

@kotlin.clr.ClrTypeAlias("System.Reflection.FieldInfo")
private interface EnumRuntimeField {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Name")
    val name: String

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "IsStatic")
    val isStatic: Boolean

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "IsLiteral")
    val isLiteral: Boolean

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "IsPublic")
    val isPublic: Boolean

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "IsFamily")
    val isFamily: Boolean

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "IsFamilyOrAssembly")
    val isFamilyOrAssembly: Boolean

    @kotlin.clr.ClrIntrinsic("GetValue")
    fun getValue(receiver: Any?): Any?

    @kotlin.clr.ClrIntrinsic("GetCustomAttributesData")
    fun getCustomAttributesData(): EnumRuntimeList<EnumRuntimeAttribute>

}

@kotlin.clr.ClrTypeAlias("System.Reflection.BindingFlags")
@kotlin.clr.ClrEnum
private enum class EnumRuntimeBindingFlags(value: Int) {
    DECLARED_STATIC_PUBLIC(26),
    DECLARED_STATIC_PUBLIC_NON_PUBLIC(58),
}

@kotlin.clr.ClrTypeAlias("System.Reflection.MethodInfo")
private interface EnumRuntimeMethod {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Name")
    val name: String
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "ReturnType")
    val returnType: EnumRuntimeType
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "IsGenericMethod")
    val isGenericMethod: Boolean
    @kotlin.clr.ClrIntrinsic("GetParameters")
    fun getParameters(): Array<StarProjectionParameter>
    @kotlin.clr.ClrIntrinsic("GetCustomAttributesData")
    fun getCustomAttributesData(): EnumRuntimeList<EnumRuntimeAttribute>
    @kotlin.clr.ClrIntrinsic("Invoke")
    fun invoke(receiver: Any?, arguments: Array<Any?>): Any?
}

@kotlin.clr.ClrTypeAlias("System.Type")
private interface EnumRuntimeType {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "FullName")
    val fullName: String?

    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Assembly")
    val assembly: EnumRuntimeAssembly

    @kotlin.clr.ClrIntrinsic("GetFields")
    fun getFields(flags: EnumRuntimeBindingFlags): Array<EnumRuntimeField>

    @kotlin.clr.ClrIntrinsic("GetMethods")
    fun getMethods(flags: EnumRuntimeBindingFlags): Array<EnumRuntimeMethod>

    @kotlin.clr.ClrIntrinsic("MakeArrayType")
    fun makeArrayType(): EnumRuntimeType

    @kotlin.clr.ClrIntrinsic("GetCustomAttributesData")
    fun getCustomAttributesData(): EnumRuntimeList<EnumRuntimeAttribute>
}

@kotlin.clr.ClrTypeAlias("System.Reflection.Assembly")
private interface EnumRuntimeAssembly {
    @kotlin.clr.ClrIntrinsic("GetCustomAttributesData")
    fun getCustomAttributesData(): EnumRuntimeList<EnumRuntimeAttribute>
}

@kotlin.clr.ClrTypeAlias("System.Reflection.AssemblyMetadataAttribute")
private interface EnumAssemblyMarker

@kotlin.clr.ClrTypeAlias("System.Runtime.CompilerServices.CompilerGeneratedAttribute")
private interface EnumCarrierMarker

@kotlin.clr.ClrTypeAlias("System.Array")
private interface EnumRuntimeArray {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Length")
    val length: Int

    @kotlin.clr.ClrIntrinsic("SetValue")
    fun setValue(value: Any?, index: Int)
}

@kotlin.clr.ClrIntrinsic("GetType")
private fun Any.enumRuntimeType(): EnumRuntimeType = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Enum.GetValues")
private fun enumRuntimeValues(type: EnumRuntimeType): EnumRuntimeArray = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Array.CreateInstance")
private fun enumRuntimeArray(type: EnumRuntimeType, length: Int): EnumRuntimeArray = TODO("clr binding should be implemented")

private const val BASIC_ENUM = "DotKt.Runtime.CompilerServices.KotlinBasicEnumAttribute"
private const val BASIC_ENUM_ORDINAL = "DotKt.Runtime.CompilerServices.KotlinBasicEnumOrdinalAttribute"
private const val RICH_ENUM = "DotKt.Runtime.CompilerServices.KotlinRichEnumAttribute"

@PublishedApi
internal fun kotlinEnumCompareTo(receiver: Any, other: Any): Int {
    if (receiver is KotlinEnumComparison) return receiver.compareEnumTo(other)
    if (receiver.enumRuntimeType() != other.enumRuntimeType()) throw ClassCastException()
    return kotlinEnumOrdinal(receiver) - kotlinEnumOrdinal(other)
}

@PublishedApi
internal fun kotlinEnumOrdinal(value: Any): Int {
    val type = value.enumRuntimeType()
    // .NET 7+ returns fields in metadata order, matching the FieldDef sequence used by dll2klib.
    val fields = type.getFields(EnumRuntimeBindingFlags.DECLARED_STATIC_PUBLIC_NON_PUBLIC)
    val explicit = type.hasExplicitOrder()
    var ordinal = 0
    for (field in fields) {
        if (!field.isProjectedEntry(explicit)) continue
        if (field.getValue(null) == value) return if (explicit) field.declarationOrdinal(type.assembly) else ordinal
        ordinal++
    }
    return -1
}

@PublishedApi
internal fun kotlinEnumValues(typeToken: Any): Any {
    val type = typeToken as EnumRuntimeType
    // A generic caller cannot select a rich enum's class API statically. Read the exact producer-owned map;
    // neither a values() naming convention nor the singleton fields' physical order is a declaration fact.
    if (type.assembly.isCompilerAssembly()) {
        val attributes = type.getCustomAttributesData()
        for (index in 0 until attributes.count) {
            val attribute = attributes[index]
            if (!attribute.isCompilerCarrier(RICH_ENUM, type.assembly)) continue
            val api = richEnumValuesApi(attribute)
            val method = type.enumValuesMethod(api)
            try { return method.invoke(null, emptyArray())!! }
            catch (failure: StarProjectionInvocationException) { throw (failure.innerException ?: failure) }
        }
    }
    // GetValues supplies a native enum array, not its Kotlin ordering. Overwrite every projected slot below.
    val nativeValues = enumRuntimeValues(type)
    val fields = type.getFields(EnumRuntimeBindingFlags.DECLARED_STATIC_PUBLIC_NON_PUBLIC)
    val explicit = type.hasExplicitOrder()
    var count = 0
    for (field in fields) if (field.isProjectedEntry(explicit)) count++
    val values = if (nativeValues.length == count) nativeValues else enumRuntimeArray(type, count)
    var ordinal = 0
    for (field in fields) {
        if (!field.isProjectedEntry(explicit)) continue
        val index = if (explicit) field.declarationOrdinal(type.assembly) else ordinal
        values.setValue(field.getValue(null), index)
        ordinal++
    }
    return values
}

private fun EnumRuntimeType.enumValuesMethod(api: String): EnumRuntimeMethod {
    val valuesType = makeArrayType()
    val markerType = EnumCarrierMarker::class as EnumRuntimeType
    var selected: EnumRuntimeMethod? = null
    for (method in getMethods(EnumRuntimeBindingFlags.DECLARED_STATIC_PUBLIC)) {
        if (method.name != api || method.returnType != valuesType || method.isGenericMethod ||
            method.getParameters().size != 0) continue
        val attributes = method.getCustomAttributesData()
        for (index in 0 until attributes.count) {
            if (attributes[index].attributeType != markerType) continue
            check(selected == null)
            selected = method
            break
        }
    }
    return selected!!
}

@kotlin.clr.ClrTypeAlias("System.Text.Encoding")
private interface EnumCarrierEncoding {
    @OptIn(ExperimentalUnsignedTypes::class)
    @kotlin.clr.ClrIntrinsic("GetString")
    fun getString(bytes: UByteArray): String
}

@kotlin.clr.ClrIntrinsic("System.Text.Encoding.get_UTF8")
private fun enumCarrierEncoding(): EnumCarrierEncoding = error("CLR binding")

@kotlin.clr.ClrTypeAlias("System.Text.Json.JsonDocumentOptions")
private interface EnumCarrierJsonOptions

@kotlin.clr.ClrTypeAlias("System.Text.Json.JsonDocument")
private interface EnumCarrierJsonDocument {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "RootElement")
    val root: EnumCarrierJsonElement
    @kotlin.clr.ClrIntrinsic("Dispose")
    fun dispose()
}

@kotlin.clr.ClrTypeAlias("System.Text.Json.JsonElement")
private interface EnumCarrierJsonElement {
    @kotlin.clr.ClrIntrinsic("GetProperty")
    fun getProperty(name: String): EnumCarrierJsonElement
    @kotlin.clr.ClrIntrinsic("GetString")
    fun getString(): String?
}

@kotlin.clr.ClrIntrinsic("System.Text.Json.JsonDocument.Parse")
private fun enumCarrierJson(json: String, options: EnumCarrierJsonOptions): EnumCarrierJsonDocument = error("CLR binding")

@kotlin.clr.ClrIntrinsic("System.Activator.CreateInstance")
private fun <T> enumCarrierDefault(): T = error("CLR binding")

@OptIn(ExperimentalUnsignedTypes::class)
private fun richEnumValuesApi(attribute: EnumRuntimeAttribute): String {
    val arguments = attribute.constructorArguments
    check(arguments[0].value == "bir-json/1")
    @Suppress("UNCHECKED_CAST")
    val content = arguments[1].value as EnumRuntimeList<EnumRuntimeAttributeArgument>
    val bytes = UByteArray(content.count) { content[it].value as UByte }
    val document = enumCarrierJson(enumCarrierEncoding().getString(bytes), enumCarrierDefault<EnumCarrierJsonOptions>())
    try { return document.root.getProperty("values").getString()!! }
    finally { document.dispose() }
}

private fun EnumRuntimeField.isProjectedEntry(explicit: Boolean): Boolean =
    isStatic && isLiteral && (explicit || !name.startsWith("<")) && (isPublic || isFamily || isFamilyOrAssembly)

private fun EnumRuntimeType.hasExplicitOrder(): Boolean {
    val attributes = getCustomAttributesData()
    for (index in 0 until attributes.count)
        if (attributes[index].isCompilerCarrier(BASIC_ENUM, assembly) && assembly.isCompilerAssembly()) return true
    return false
}

private fun EnumRuntimeField.declarationOrdinal(owner: EnumRuntimeAssembly): Int {
    val attributes = getCustomAttributesData()
    for (index in 0 until attributes.count) {
        val attribute = attributes[index]
        if (attribute.isCompilerCarrier(BASIC_ENUM_ORDINAL, owner))
            return attribute.constructorArguments[0].value as Int
    }
    return -1
}

private fun EnumRuntimeAttribute.isCompilerCarrier(name: String, owner: EnumRuntimeAssembly): Boolean {
    val type = attributeType
    if (type.fullName != name || type.assembly != owner) return false
    val attributes = type.getCustomAttributesData()
    val markerType = EnumCarrierMarker::class as EnumRuntimeType
    for (index in 0 until attributes.count)
        if (attributes[index].attributeType == markerType) return true
    return false
}

private fun EnumRuntimeAssembly.isCompilerAssembly(): Boolean {
    val attributes = getCustomAttributesData()
    val markerType = EnumAssemblyMarker::class as EnumRuntimeType
    for (index in 0 until attributes.count) {
        val attribute = attributes[index]
        if (attribute.attributeType != markerType) continue
        val arguments = attribute.constructorArguments
        if (arguments.count == 2 && arguments[0].value == "DotKt.Compiler" && arguments[1].value == "metadata-v1") return true
    }
    return false
}
