import InheritedOptionalDefaults.BaseWriter
import InheritedOptionalDefaults.DerivedWriter
import InheritedOptionalDefaults.DerivedValueTypeDefaults
import InheritedOptionalDefaults.EnumDefaults
import InheritedOptionalDefaults.EnumWidthDefaults
import InheritedOptionalDefaults.GenericDerivedWriter
import InheritedOptionalDefaults.HidingDerivedWriter
import InheritedOptionalDefaults.IDerivedWriter
import InheritedOptionalDefaults.InterfaceWriter
import InheritedOptionalDefaults.KeyModifiers
import InheritedOptionalDefaults.NavigationMethod
import InheritedOptionalDefaults.NullableValueDefaults
import InheritedOptionalDefaults.StaticEnumDefaults
import InheritedOptionalDefaults.ValueBaseWriter
import InheritedOptionalDefaults.ValueTypeDefaultConstructor
import InheritedOptionalDefaults.ValueTypeDefaults
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

    @TestAttribute
    fun enumOptionalValuesPreserveTheDeclaredEnumSlotAndPhysicalBits() {
        val defaults = EnumDefaults()
        assertEquals("0:0", defaults.Focus())
        assertEquals("2:5", defaults.Move())
        assertEquals("2:5", StaticEnumDefaults.Move())
        assertEquals("0:4", defaults.Move(NavigationMethod.Unspecified, KeyModifiers.Shift))
        assertEquals(
            "-128:255:-32768:65535:-2147483648:4294967295:-9223372036854775808:18446744073709551615",
            EnumWidthDefaults().Read(),
        )
    }

    @TestAttribute
    fun valueTypeOptionalValuesAreMaterializedForEveryCallShape() {
        val direct = ValueTypeDefaults()
        val inherited = DerivedValueTypeDefaults()

        assertEquals(true, direct.Instance())
        assertEquals(true, inherited.Instance())
        assertEquals(true, ValueTypeDefaults.Static())
        assertEquals(0, ValueTypeDefaults.GenericDefault<Int>())
        assertEquals(null, ValueTypeDefaults.GenericDefault<String>())
        assertEquals(true, ValueTypeDefaultConstructor().ValuesMatch)
        assertEquals(638000000000000000L, direct.DateTimeConstant())
        assertEquals("7:99", DerivedWriter().Save(7))
        assertEquals("-7:2", NullableValueDefaults().Instance())
        assertEquals("42:0", NullableValueDefaults.Static())
        assertEquals("null", NullableValueDefaults.ReferenceNull())
        assertEquals(true, NullableValueDefaults.NonFinite())
    }

    private fun nullableBase(value: BaseWriter): BaseWriter? = value
    private fun nullableDerived(value: DerivedWriter): DerivedWriter? = value
}
