import NUnit.Framework.TestAttribute
import roundtrip.mutablepolicy.PortableMutableList
import roundtrip.mutablepolicy.PortableMutableSet
import roundtrip.mutablepolicy.PortableMutableCollection
import roundtrip.mutablepolicy.PortableSuperRenderList

class CrossModulePortableMutableConcreteRenderingTests {
    @TestAttribute fun concreteAndSuperCallsReachTheInheritedBody() {
        val list = PortableMutableList(mutableListOf<Int?>(1, null, 2))
        check(list.toString() == "[1, null, 2]")
        val set = PortableMutableSet(mutableListOf(3, 4))
        check(set.toString() == "[3, 4]")
        val collection = PortableMutableCollection(mutableListOf(5, 6))
        check(collection.toString() == "[5, 6]")
        val superValue: Any = PortableSuperRenderList(mutableListOf<Int?>(1, null, 2))
        check(superValue.toString() == "super:[1, null, 2]")
    }
}
