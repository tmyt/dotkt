import InheritedDefaultSlots.DefaultBase
import InheritedDefaultSlots.Driver
import InheritedDefaultSlots.IntroducesVirtualBody
import InheritedDefaultSlots.VirtualBase
import InheritedDefaultSlots.WithProperty
import InheritedDefaultSlots.ITextChannel
import InheritedDefaultSlots.TextChannel
import InheritedDefaultSlots.HiddenChannel
import InheritedDefaultSlots.ExplicitChannel
import InheritedDefaultSlots.ExplicitDefaultBase
import InheritedDefaultSlots.NewDefaultNamesake
import NUnit.Framework.TestAttribute

private class InheritVirtualBody : VirtualBase()
private class PreserveExplicitDefault : ExplicitDefaultBase<String>() {
    override fun Read(key: String): Any = "exact"
}
private class CovariantNamesake : ExplicitDefaultBase<String>() {
    override fun Read(key: String): String = "covariant"
}
private class InheritNewNamesake : NewDefaultNamesake()
private class InheritChannel : TextChannel(), ITextChannel
private class UseNewVirtualSlot : HiddenChannel(), ITextChannel
private class ReimplementChannel : ExplicitChannel(), ITextChannel {
    override fun Read(key: String): Int = 7
}
private class OverrideClassSlotOnly : ExplicitChannel() {
    override fun Read(key: String): Int = 8
}
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
        val explicit = PreserveExplicitDefault()
        check(Driver.ReadDefaultBase(explicit) == "exact" && Driver.ReadDefaultChannel(explicit) == "explicit")
        val covariant = CovariantNamesake()
        check(Driver.ReadDefaultBase(covariant) == "covariant" && Driver.ReadDefaultChannel(covariant) == "explicit")
        val inherited = InheritNewNamesake()
        check(inherited.Read("x") == "new" && Driver.ReadDefaultChannel(inherited) == "explicit")
        check(Driver.CheckChannel(InheritChannel(), UseNewVirtualSlot()))
        val authored = ReimplementChannel()
        check(authored.Read("x") == 7 && Driver.ReadChannel(authored) == 7 && Driver.ReadText(authored) == 7)
        val classOnly = OverrideClassSlotOnly()
        check(classOnly.Read("x") == 8 && Driver.ReadChannel(classOnly) == 4)
        val introduced = InheritIntroducedBody()
        check(introduced.Read() == 70 && Driver.ReadInterface(introduced) == 70)
        val sibling = ReplaceBesideGetter()
        check(sibling.get_Value() == 4 && Driver.ReadProperty(sibling) == 2)
    }
}
