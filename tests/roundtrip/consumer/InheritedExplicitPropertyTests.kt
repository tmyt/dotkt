package roundtriptests.inheritedproperties

import NUnit.Framework.TestAttribute
import InheritedPropertyInterop.IValue
import roundtrip.inheritedproperties.*

private class FurtherPropertySubclass : ImportedPropertySubclass()

class InheritedExplicitPropertyTests {
    @TestAttribute fun importedSubclassRetainsThePublicInheritedProperty() {
        val value = ImportedPropertySubclass()
        check(value.Value == "public")
        check(readInheritedPublicProperty(value) == "public")
        check((value as IValue<String>).Value == "slot")
    }

    @TestAttribute fun furtherSubclassRetainsBothPropertySlots() {
        val value = FurtherPropertySubclass()
        check(value.Value == "public")
        check((value as IValue<String>).Value == "slot")
    }
}
