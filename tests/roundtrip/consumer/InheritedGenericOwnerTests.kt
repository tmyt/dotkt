package roundtriptests.inheritedowner

import NUnit.Framework.TestAttribute
import roundtrip.inheritedowner.*
import InheritedGenericOwners.Outer1.Contract as OwnContract
import InheritedGenericOwners.Outer.Contract as InheritedContract
import InheritedGenericOwners.OffsetBase
import InheritedGenericOwners.OffsetCalls

private class MixedOffsets : OffsetBase(), OwnContract<Int, String>, InheritedContract<Int, String> {
    override fun get_Offset(): Int = 19
}

private fun <T : Any> importedEcho(value: ImportedGenericOwner<T>, item: T): T = value.Echo(item)

class InheritedGenericOwnerTests {
    @TestAttribute fun sameFlattenedArityDoesNotMergeDistinctNestedInterfaceSlots() {
        val mixed = MixedOffsets()
        check(OffsetCalls.Own(mixed) == 19)
        check(OffsetCalls.Inherited(mixed) == 7)
    }

    @TestAttribute fun importedSubclassRetainsClosedOwnerAndMethodArguments() {
        val value = ImportedIntOwner()
        check(value.Echo(7) == 7)
        check(value.Convert(9, "imported") == "imported")
        val echo = value::Echo
        check(echo(11) == 11)
    }

    @TestAttribute fun importedGenericSubclassRetainsItsOpenFrame() {
        val strings = ImportedGenericOwner<String>()
        val integers = ImportedGenericOwner<Int>()
        check(importedEcho(strings, "value") == "value")
        check(importedEcho(integers, 9) == 9)
        check(strings.forward("self") == "self")
        check(integers.forward(11) == 11)
    }

    @TestAttribute fun importedNestedBaseRetainsBothOwnerArguments() {
        val value = ImportedNestedOwner()
        check(value.Outer("outer") == "outer")
        check(value.Inner(9) == 9)
    }
}
