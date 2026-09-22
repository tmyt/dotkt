package protecteddefaults

class Child : ProtectedDefaultAccess.Base() {
    fun staticDefault(value: Int = ProtectedDefaultAccess.Base.StaticField): Int = value
    fun instanceDefault(value: Int = InstanceField): Int = value
    fun propertyDefault(value: Int = Property): Int = value
    fun methodDefault(value: Int = Method()): Int = value
}

class GenericChild<T>(value: T) : ProtectedDefaultAccess.GenericBase<T>(value) {
    fun instanceDefault(value: T = Value): T = value
}
