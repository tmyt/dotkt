import NUnit.Framework.TestAttribute

class ProtectedDefaultAccessRoundtripTests {
    @TestAttribute fun importedDefaultsKeepProtectedAccessFromUnrelatedCaller() {
        val child = protecteddefaults.Child()
        check(child.staticDefault() == 181)
        check(child.instanceDefault() == 191)
        check(child.propertyDefault() == 193)
        check(child.methodDefault() == 197)
        check(child.readonlyDefault() == 241)
        check(child.volatileDefault() == 251)
        check(child.staticDefault(229) == 229)
        check(child.instanceDefault(233) == 233)
    }

    @TestAttribute fun importedDefaultsKeepConstructedGenericFieldDeclaration() {
        check(protecteddefaults.GenericChild("imported").instanceDefault() == "imported")
        check(protecteddefaults.GenericChild(239).instanceDefault() == 239)
        val child = protecteddefaults.StringChild("static imported")
        check(child.echoDefault() == "default")
        check(child.echoCall() == "direct")
        check(child.staticDefault() == "static imported")
        val mapped = protecteddefaults.MappedFieldChild("mapped imported")
        check(mapped.read() == "mapped imported")
        mapped.write("written imported")
        check(mapped.read() == "written imported")
        check((mapped as ProtectedDefaultAccess.IExplicitValue).Value == 283)
    }
}
