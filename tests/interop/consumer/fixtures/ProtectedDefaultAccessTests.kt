import NUnit.Framework.TestAttribute

private class LocalProtectedDefaultChild : ProtectedDefaultAccess.Base() {
    fun staticDefault(value: Int = ProtectedDefaultAccess.Base.StaticField): Int = value
    fun instanceDefault(value: Int = InstanceField): Int = value
    fun propertyDefault(value: Int = Property): Int = value
    fun methodDefault(value: Int = Method()): Int = value
}

private class LocalProtectedGenericDefaultChild<T>(value: T) : ProtectedDefaultAccess.GenericBase<T>(value) {
    fun instanceDefault(value: T = Value): T = value
}

class ProtectedDefaultAccessTests {
    @TestAttribute fun defaultsKeepProtectedAccessFromUnrelatedCaller() {
        val child = LocalProtectedDefaultChild()
        check(child.staticDefault() == 181)
        check(child.instanceDefault() == 191)
        check(child.propertyDefault() == 193)
        check(child.methodDefault() == 197)
        check(child.staticDefault(211) == 211)
        check(child.instanceDefault(223) == 223)
    }

    @TestAttribute fun defaultsKeepConstructedGenericFieldDeclaration() {
        check(LocalProtectedGenericDefaultChild("local").instanceDefault() == "local")
        check(LocalProtectedGenericDefaultChild(227).instanceDefault() == 227)
    }
}
