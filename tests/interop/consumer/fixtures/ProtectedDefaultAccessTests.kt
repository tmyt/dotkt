import NUnit.Framework.TestAttribute

private class LocalProtectedDefaultChild : ProtectedDefaultAccess.Base() {
    fun staticDefault(value: Int = ProtectedDefaultAccess.Base.StaticField): Int = value
    fun instanceDefault(value: Int = InstanceField): Int = value
    fun propertyDefault(value: Int = Property): Int = value
    fun methodDefault(value: Int = Method()): Int = value
    fun readonlyDefault(value: Int = ReadonlyField): Int = value
    fun volatileDefault(value: Int = VolatileField): Int = value
}

private class LocalProtectedGenericDefaultChild<T>(value: T) : ProtectedDefaultAccess.GenericBase<T>(value) {
    fun instanceDefault(value: T = Value): T = value
}

private class LocalProtectedStringDefaultChild(value: String) : ProtectedDefaultAccess.GenericBase<String>(value) {
    fun echoDefault(value: String = Echo("default")): String = value
    fun echoCall(): String = Echo("direct")
    fun staticDefault(value: String = LocalProtectedStringDefaultChild.StaticValue): String = value
}

private class LocalMappedFieldChild<T>(value: T) : ProtectedDefaultAccess.MappedField<Int, T>(value) {
    fun read(value: T = Value): T = value
    fun write(value: T) { Value = value }
}

class ProtectedDefaultAccessTests {
    @TestAttribute fun defaultsKeepProtectedAccessFromUnrelatedCaller() {
        val child = LocalProtectedDefaultChild()
        check(child.staticDefault() == 181)
        check(child.instanceDefault() == 191)
        check(child.propertyDefault() == 193)
        check(child.methodDefault() == 197)
        check(child.readonlyDefault() == 241)
        check(child.volatileDefault() == 251)
        check(child.staticDefault(211) == 211)
        check(child.instanceDefault(223) == 223)
    }

    @TestAttribute fun defaultsKeepConstructedGenericFieldDeclaration() {
        check(LocalProtectedGenericDefaultChild("local").instanceDefault() == "local")
        check(LocalProtectedGenericDefaultChild(227).instanceDefault() == 227)
        val child = LocalProtectedStringDefaultChild("static local")
        check(child.echoDefault() == "default")
        check(child.echoCall() == "direct")
        check(child.staticDefault() == "static local")
        val mapped = LocalMappedFieldChild("mapped local")
        check(mapped.read() == "mapped local")
        mapped.write("written local")
        check(mapped.read() == "written local")
        check((mapped as ProtectedDefaultAccess.IExplicitValue).Value == 283)
    }
}
