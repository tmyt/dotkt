package roundtriptests.constructorkinds

import NUnit.Framework.TestAttribute
import roundtrip.constructorkinds.*

@ConstructorKindAnnotation(3)
class ConstructorKindTests {
    @TestAttribute
    fun sourceKindsKeepDefaultOverloadAndInnerDelegationAcrossDlls() {
        check(PrimaryAndSecondary().value == 11)
        check(PrimaryAndSecondary("four").value == 4)
        check(OnlySecondary().value == 17 && OnlySecondary(23).value == 23)
        val outer = GenericConstructorOuter("original")
        check(outer.Inner().stored == "original")
        check(outer.Inner("explicit").stored == "explicit")
    }
}
