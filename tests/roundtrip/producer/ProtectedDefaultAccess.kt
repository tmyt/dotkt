package protecteddefaults

class Child : ProtectedDefaultAccess.Base() {
    fun staticDefault(value: Int = ProtectedDefaultAccess.Base.StaticField): Int = value
    fun instanceDefault(value: Int = InstanceField): Int = value
    fun propertyDefault(value: Int = Property): Int = value
    fun methodDefault(value: Int = Method()): Int = value
    fun readonlyDefault(value: Int = ReadonlyField): Int = value
    fun volatileDefault(value: Int = VolatileField): Int = value
}

class GenericChild<T>(value: T) : ProtectedDefaultAccess.GenericBase<T>(value) {
    fun instanceDefault(value: T = Value): T = value
}

class StringChild(value: String) : ProtectedDefaultAccess.GenericBase<String>(value) {
    fun echoDefault(value: String = Echo("default")): String = value
    fun echoCall(): String = Echo("direct")
    fun staticDefault(value: String = StringChild.StaticValue): String = value
}

class MappedFieldChild<T>(value: T) : ProtectedDefaultAccess.MappedField<Int, T>(value) {
    fun read(value: T = Value): T = value
    fun write(value: T) { Value = value }
}
