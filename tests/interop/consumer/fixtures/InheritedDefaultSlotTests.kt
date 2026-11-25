import InheritedDefaultSlots.DefaultBase
import InheritedDefaultSlots.Driver
import InheritedDefaultSlots.IntroducesVirtualBody
import InheritedDefaultSlots.VirtualBase
import InheritedDefaultSlots.WithProperty
import NUnit.Framework.TestAttribute

private class InheritVirtualBody : VirtualBase()
private class InheritIntroducedBody : IntroducesVirtualBody()
private class ReplaceInterfaceBody : DefaultBase() {
    override fun Read(): Int = 3
}
private open class ReplaceBesideGetter : WithProperty() {
    override fun Read(): Int = 3
    open fun get_Value(): Int = 4
}

class InheritedDefaultSlotTests {
    @TestAttribute
    fun inheritedAndReimplementedInterfaceMaps() {
        check(Driver.Check(InheritVirtualBody(), ReplaceInterfaceBody()))
        val introduced = InheritIntroducedBody()
        check(introduced.Read() == 70 && Driver.ReadInterface(introduced) == 70)
        val sibling = ReplaceBesideGetter()
        check(sibling.get_Value() == 4 && Driver.ReadProperty(sibling) == 2)
    }
}
