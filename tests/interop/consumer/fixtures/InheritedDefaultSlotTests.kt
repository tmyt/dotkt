import InheritedDefaultSlots.DefaultBase
import InheritedDefaultSlots.Driver
import InheritedDefaultSlots.IntroducesVirtualBody
import InheritedDefaultSlots.VirtualBase
import InheritedDefaultSlots.WithProperty
import InheritedDefaultSlots.ITextChannel
import InheritedDefaultSlots.TextChannel
import InheritedDefaultSlots.HiddenChannel
import InheritedDefaultSlots.ExplicitChannel
import InheritedDefaultSlots.ExplicitPropertyBase
import InheritedDefaultSlots.ExplicitDefaultBase
import InheritedDefaultSlots.NewDefaultNamesake
import InheritedDefaultSlots.KeyBox
import InheritedDefaultSlots.SlotFamily
import NUnit.Framework.TestAttribute

private class InheritVirtualBody : VirtualBase()
private class PreserveExplicitDefault : ExplicitDefaultBase<String>() {
    override fun Read(key: String): Any = "exact"
}
private class CovariantNamesake : ExplicitDefaultBase<String>() {
    override fun Read(key: String): String = "covariant"
}
private open class ReplaceDefaultBesideExplicit : ExplicitDefaultBase<KeyBox<String>>() {
    override fun Read(key: KeyBox<String>): Any = "class"
    override fun Tick(): Int = 9
}
private class DerivedReplacement : ReplaceDefaultBesideExplicit() {
    override fun Read(key: KeyBox<String>): Any = "derived"
}
private class ReplaceProtectedDefault : SlotFamily.Native() { override fun Tick(): Int = 9 }
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
private class GenericReplacement<T> : DefaultBase() {
    override fun Read(): Int = 9
}
private class GenericCovariant<T> : ExplicitDefaultBase<String>() {
    override fun Read(key: String): String = "subclass"
}
private class GenericGetter<T> : ExplicitPropertyBase() {
    override val Value: Int get() = 8
}
private open class ReplaceBesideGetter : WithProperty() {
    override fun Read(): Int = 3
    open fun get_Value(): Int = 4
}

class InheritedDefaultSlotTests {
    @TestAttribute
    fun inheritedAndReimplementedInterfaceMaps() {
        check(Driver.Check(InheritVirtualBody(), ReplaceInterfaceBody()))
        val generic = GenericReplacement<String>()
        check(generic.Read() == 9 && Driver.ReadInterface(generic) == 9)
        val result = GenericCovariant<String>()
        check(Driver.ReadDefaultBase(result) == "subclass" && Driver.ReadDefaultChannel(result) == "explicit")
        val getter = GenericGetter<String>()
        check(getter.Value == 8 && Driver.ReadExplicitProperty(getter) == 4)
        val explicit = PreserveExplicitDefault()
        check(Driver.ReadDefaultBase(explicit) == "exact" && Driver.ReadDefaultChannel(explicit) == "explicit")
        val covariant = CovariantNamesake()
        check(Driver.ReadDefaultBase(covariant) == "covariant" && Driver.ReadDefaultChannel(covariant) == "explicit")
        for ((instance, expected) in listOf(ReplaceDefaultBesideExplicit() to "class", DerivedReplacement() to "derived")) {
            check(instance.Read(KeyBox<String>()) == expected && Driver.ReadBoxBase(instance) == expected)
            check(Driver.ReadBoxChannel(instance) == "explicit" && Driver.Tick(instance) == 9)
        }
        check(SlotFamily.Tick(ReplaceProtectedDefault()) == 9)
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
