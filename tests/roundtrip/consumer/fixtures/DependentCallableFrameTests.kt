import NUnit.Framework.TestAttribute
import roundtrip.dependentframes.Factory
import roundtrip.dependentframes.Item
import roundtrip.dependentframes.IntItem
import roundtrip.dependentframes.Owner

class DependentCallableFrameTests {
    @TestAttribute fun importedDependentOwnerKeepsBoundAndUnboundTargets() {
        val owner = Owner<String, Item>()
        val first = Item()
        val second = Item()
        check(owner.unbound(first, first) == 1)
        check(owner.bound(first, second) == -2)
        check(owner.unbound(second, second) == 1)
        check(first.calls == 2 && second.calls == 1)
        val integerTagged = IntItem()
        check(Owner<Int, IntItem>().unbound(integerTagged, integerTagged) == 1)
    }

    @TestAttribute fun dependentOwnerCanBeConstructedFromAnotherGenericOwner() {
        Factory<String, Item>().constructOnly()
        Factory<Int, IntItem>().constructOnly()
        val value = Item()
        check(Owner<String, Item>().unbound(value, value) == 1)
    }
}
