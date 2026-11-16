import ConstructorKindInterop.NativeConstructorKindAttribute
import ConstructorKindInterop.NativeConstructorKinds
import NUnit.Framework.TestAttribute

@NativeConstructorKindAttribute(5)
class NativeConstructorKindTests {
    @TestAttribute
    fun ordinaryNativeOverloadsAndAnnotationConstructorsRemainCallable() {
        check(NativeConstructorKinds().Value == 7)
        check(NativeConstructorKinds(29).Value == 29)
    }
}
