import NUnit.Framework.TestAttribute

class ProtectedDefaultAccessRoundtripTests {
    @TestAttribute fun importedDefaultsKeepProtectedAccessFromUnrelatedCaller() {
        val child = protecteddefaults.Child()
        check(child.staticDefault() == 181)
        check(child.instanceDefault() == 191)
        check(child.propertyDefault() == 193)
        check(child.methodDefault() == 197)
        check(child.staticDefault(229) == 229)
        check(child.instanceDefault(233) == 233)
    }

    @TestAttribute fun importedDefaultsKeepConstructedGenericFieldDeclaration() {
        check(protecteddefaults.GenericChild("imported").instanceDefault() == "imported")
        check(protecteddefaults.GenericChild(239).instanceDefault() == 239)
    }
}
