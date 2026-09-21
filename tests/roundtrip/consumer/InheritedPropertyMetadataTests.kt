package roundtriptests.propertymetadata

import NUnit.Framework.TestAttribute
import PropertyMetadataInterop.*
import roundtrip.propertymetadata.Box

class InheritedPropertyMetadataTests {
    @TestAttribute fun carrierRestorationUsesTheBaseDeclarationFrame() {
        val value = CarrierMix<Int, String>("public")
        val inherited: Box<List<String>> = value.Value
        check(inherited.item[0] == "public")
        check((value as IValue<Int>).Value == 17)
    }

    @TestAttribute fun innerArgumentsUseTheDeclarationVariableOrder() {
        val value = InnerMix()
        val inherited: String = value.Value
        check(inherited == "outer")
        check((value as IValue<Int>).Value == 17)
    }

    @TestAttribute fun nrtAnnotationsStayOnTheDeclaredTypeTree() {
        val value = NrtMix()
        check(value.Value.Item2 == null)
        check((value as IValue<Int>).Value == 17)
    }
}
