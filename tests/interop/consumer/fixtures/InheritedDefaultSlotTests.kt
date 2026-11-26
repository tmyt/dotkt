import InheritedDefaultSlots.DefaultBase
import InheritedDefaultSlots.Driver
import InheritedDefaultSlots.IntroducesVirtualBody
import InheritedDefaultSlots.VirtualBase
import InheritedDefaultSlots.WithProperty
import InheritedDefaultSlots.ITextChannel
import InheritedDefaultSlots.TextChannel
import InheritedDefaultSlots.HiddenChannel
import NUnit.Framework.TestAttribute

private class InheritVirtualBody : VirtualBase()
private class InheritChannel : TextChannel(), ITextChannel
private class UseNewVirtualSlot : HiddenChannel(), ITextChannel
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
        check(Driver.CheckChannel(InheritChannel(), UseNewVirtualSlot()))
        val introduced = InheritIntroducedBody()
        check(introduced.Read() == 70 && Driver.ReadInterface(introduced) == 70)
        val sibling = ReplaceBesideGetter()
        check(sibling.get_Value() == 4 && Driver.ReadProperty(sibling) == 2)
    }
}
