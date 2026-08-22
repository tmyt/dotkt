import InheritedOptionalDefaults.BaseWriter
import InheritedOptionalDefaults.DerivedWriter
import InheritedOptionalDefaults.GenericDerivedWriter
import InheritedOptionalDefaults.HidingDerivedWriter
import InheritedOptionalDefaults.IDerivedWriter
import InheritedOptionalDefaults.InterfaceWriter
import InheritedOptionalDefaults.ValueBaseWriter
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

class InheritedOptionalArgumentTests {
    @TestAttribute
    fun declaringAndDerivedReceiversUseTheSelectedDeclarationsOptionalValue() {
        val base = BaseWriter()
        val derived = DerivedWriter()

        assertEquals("base:default", base.Save("base"))
        assertEquals("derived:default", derived.Save("derived"))
        assertEquals("base-safe:default", nullableBase(base)?.Save("base-safe"))
        assertEquals("derived-safe:default", nullableDerived(derived)?.Save("derived-safe"))
    }

    @TestAttribute
    fun explicitArgumentsRemainUnchangedThroughDeclaringAndDerivedReceivers() {
        val base = BaseWriter()
        val derived = DerivedWriter()

        assertEquals("base-value:23", base.Save("base-value", 23))
        assertEquals("derived-value:73", derived.Save("derived-value", 73))
        assertEquals("base-safe-value:41", nullableBase(base)?.Save("base-safe-value", 41))
        assertEquals("derived-safe-value:59", nullableDerived(derived)?.Save("derived-safe-value", 59))
    }

    @TestAttribute
    fun constructedGenericAndInheritedInterfaceReceiversUseTheSelectedDeclarationsDefault() {
        val generic = GenericDerivedWriter<Int>()
        val iface: IDerivedWriter = InterfaceWriter()
        val base = ValueBaseWriter()
        val hiding = HidingDerivedWriter()

        assertEquals("7:1", generic.Save(7))
        assertEquals("interface:1", iface.Save("interface"))
        assertEquals("base-value:5", base.Save("base-value"))
        assertEquals("derived-value:7", hiding.Save("derived-value"))
    }

    private fun nullableBase(value: BaseWriter): BaseWriter? = value
    private fun nullableDerived(value: DerivedWriter): DerivedWriter? = value
}
