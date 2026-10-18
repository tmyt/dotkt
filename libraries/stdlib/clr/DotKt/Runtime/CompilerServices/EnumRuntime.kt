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
    DECLARED_STATIC_PUBLIC_NON_PUBLIC(58),
}

@kotlin.clr.ClrTypeAlias("System.Type")
private interface EnumRuntimeType {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "FullName")
    val fullName: String?

    @kotlin.clr.ClrIntrinsic("GetFields")
    fun getFields(flags: EnumRuntimeBindingFlags): Array<EnumRuntimeField>

    @kotlin.clr.ClrIntrinsic("GetCustomAttributesData")
    fun getCustomAttributesData(): EnumRuntimeList<EnumRuntimeAttribute>
}

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

@PublishedApi
internal fun kotlinEnumCompareTo(receiver: Any, other: Any): Int {
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
        if (!field.isProjectedEntry()) continue
        if (field.getValue(null) == value) return if (explicit) field.declarationOrdinal() else ordinal
        ordinal++
    }
    return -1
}

@PublishedApi
internal fun kotlinEnumValues(typeToken: Any): Any {
    val type = typeToken as EnumRuntimeType
    // GetValues supplies a native enum array, not its Kotlin ordering. Overwrite every projected slot below.
    val nativeValues = enumRuntimeValues(type)
    val fields = type.getFields(EnumRuntimeBindingFlags.DECLARED_STATIC_PUBLIC_NON_PUBLIC)
    var count = 0
    for (field in fields) if (field.isProjectedEntry()) count++
    val values = if (nativeValues.length == count) nativeValues else enumRuntimeArray(type, count)
    val explicit = type.hasExplicitOrder()
    var ordinal = 0
    for (field in fields) {
        if (!field.isProjectedEntry()) continue
        val index = if (explicit) field.declarationOrdinal() else ordinal
        values.setValue(field.getValue(null), index)
        ordinal++
    }
    return values
}

private fun EnumRuntimeField.isProjectedEntry(): Boolean =
    isStatic && isLiteral && (isPublic || isFamily || isFamilyOrAssembly)

private fun EnumRuntimeType.hasExplicitOrder(): Boolean {
    val attributes = getCustomAttributesData()
    for (index in 0 until attributes.count)
        if (attributes[index].attributeType.fullName == BASIC_ENUM) return true
    return false
}

private fun EnumRuntimeField.declarationOrdinal(): Int {
    val attributes = getCustomAttributesData()
    for (index in 0 until attributes.count) {
        val attribute = attributes[index]
        if (attribute.attributeType.fullName == BASIC_ENUM_ORDINAL)
            return attribute.constructorArguments[0].value as Int
    }
    return -1
}
