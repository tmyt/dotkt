package DotKt.Runtime.CompilerServices

// Generic `T : Enum<T>` code cannot bake in one enum declaration's ordinal map. Explicit Kotlin CLR enums therefore
// stamp each literal with its declaration ordinal; this runtime reads that exact compiler carrier. Enums without the
// carrier retain the CLR projection's established Enum.GetValues order, including arbitrary external CLR enums.

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

    @kotlin.clr.ClrIntrinsic("GetValue")
    fun getValue(receiver: Any?): Any?

    @kotlin.clr.ClrIntrinsic("GetCustomAttributesData")
    fun getCustomAttributesData(): EnumRuntimeList<EnumRuntimeAttribute>
}

@kotlin.clr.ClrTypeAlias("System.Type")
private interface EnumRuntimeType {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "FullName")
    val fullName: String?

    @kotlin.clr.ClrIntrinsic("GetFields")
    fun getFields(): Array<EnumRuntimeField>

    @kotlin.clr.ClrIntrinsic("GetCustomAttributesData")
    fun getCustomAttributesData(): EnumRuntimeList<EnumRuntimeAttribute>
}

@kotlin.clr.ClrTypeAlias("System.Array")
private interface EnumRuntimeArray {
    @property:kotlin.clr.ClrProperty(kotlin.clr.READ, "Length")
    val length: Int

    @kotlin.clr.ClrIntrinsic("GetValue")
    fun getValue(index: Int): Any?
}

@kotlin.clr.ClrIntrinsic("GetType")
private fun Any.enumRuntimeType(): EnumRuntimeType = TODO("clr binding should be implemented")

@kotlin.clr.ClrIntrinsic("System.Enum.GetValues")
private fun enumRuntimeValues(type: EnumRuntimeType): EnumRuntimeArray = TODO("clr binding should be implemented")

private const val BASIC_ENUM = "DotKt.Runtime.CompilerServices.KotlinBasicEnumAttribute"
private const val BASIC_ENUM_ORDINAL = "DotKt.Runtime.CompilerServices.KotlinBasicEnumOrdinalAttribute"

@PublishedApi
internal fun kotlinEnumOrdinal(value: Any): Int {
    val type = value.enumRuntimeType()
    var explicit = false
    val typeAttributes = type.getCustomAttributesData()
    for (index in 0 until typeAttributes.count) {
        if (typeAttributes[index].attributeType.fullName == BASIC_ENUM) {
            explicit = true
            break
        }
    }
    if (explicit) {
        for (field in type.getFields()) {
            if (!field.isStatic || field.getValue(null) != value) continue
            val attributes = field.getCustomAttributesData()
            for (index in 0 until attributes.count) {
                val attribute = attributes[index]
                if (attribute.attributeType.fullName == BASIC_ENUM_ORDINAL)
                    return attribute.constructorArguments[0].value as Int
            }
        }
        return -1
    }

    val values = enumRuntimeValues(type)
    for (index in 0 until values.length)
        if (values.getValue(index) == value) return index
    return -1
}
