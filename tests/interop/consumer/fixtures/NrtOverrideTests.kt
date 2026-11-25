import NUnit.Framework.TestAttribute
import NrtOverrides.Base
import NrtOverrides.Wrapper
import NrtOverrides.GenericWrapper
import NrtOverrides.Invariant

private class InheritDescription : Wrapper()
private class InheritGeneric : GenericWrapper()

class NrtOverrideTests {
    @TestAttribute
    fun inheritedSlotDoesNotBecomeAbstractWhenAnOverrideWidensNullability() {
        val wrapper = InheritDescription()
        val base: Base = wrapper
        check(base.Describe("ok") == "ok")
        check(wrapper.Describe(null) == "missing")
        check(wrapper.Describe(42) == "42")
        check(wrapper.Required("ok") == "ok")
        check(wrapper.Make(null).length == 4)
        check(base.Make("base") == "base")
        check(wrapper[null] == "indexed")
        val generic = InheritGeneric()
        check(generic.Choose(null, 0) == "first")
        check(generic.Choose(0, null) == "second")
        check(generic.Count(Invariant<String>()) == 1)
    }
}
