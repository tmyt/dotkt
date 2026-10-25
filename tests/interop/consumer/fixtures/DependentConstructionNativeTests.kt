package interop.dependentconstruction

import DependentConstructionInterop.IMarker
import DependentConstructionInterop.NativeNumber
import DependentConstructionInterop.NativeOwner
import NUnit.Framework.TestAttribute

private class ReferenceItem : IMarker<String>

private fun <Tag, T> constructNative(value: T): T where T : IMarker<Tag> =
    NativeOwner<Tag, T>().Echo(value)

class DependentConstructionNativeTests {
    @TestAttribute fun exactNativeConstraintsPermitReferenceAndStructArguments() {
        val reference = ReferenceItem()
        check(constructNative<String, ReferenceItem>(reference) === reference)
        check(constructNative<Int, NativeNumber>(NativeNumber(23)).Number == 23)
    }
}
