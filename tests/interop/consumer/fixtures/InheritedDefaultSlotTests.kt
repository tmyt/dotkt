import InheritedDefaultSlots.DefaultBase
import InheritedDefaultSlots.Driver
import InheritedDefaultSlots.VirtualBase
import NUnit.Framework.TestAttribute

private class InheritVirtualBody : VirtualBase()
private class ReplaceInterfaceBody : DefaultBase() {
    override fun Read(): Int = 3
}

class InheritedDefaultSlotTests {
    @TestAttribute
    fun inheritedAndReimplementedInterfaceMaps() {
        check(Driver.Check(InheritVirtualBody(), ReplaceInterfaceBody()))
    }
}
