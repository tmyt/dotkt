import NUnit.Framework.TestAttribute
import ExplicitFieldSlots.*

private class FieldSlotSubclass : InheritedField()
private class GenericFieldSlotSubclass : GenericField<String, Int>("derived", 43)
private class ProtectedFieldSlotSubclass : ProtectedField() {
    fun read(): String = Value
    fun write(value: String) { Value = value }
}
private class FieldSlotReimplementation : SameTypeField(), IMutableValue<Int> {
    override var Value: Int = 47
}

class ExplicitFieldSlotTests {
    @TestAttribute fun declaredFieldAndExplicitPropertyKeepIndependentStorage() {
        val value = DirectField()
        check(value.Value == "direct")
        val slot: IMutableValue<Int> = value
        check(slot.Value == 11)
        value.Value = "written"
        slot.Value = 12
        check(value.Value == "written" && slot.Value == 12)
    }

    @TestAttribute fun inheritedFieldSurvivesHiddenInterfaceCompletion() {
        val value = InheritedField()
        check(value.Value == "base")
        value.Value = "written"
        val slot: IMutableValue<Int> = value
        slot.Value = 14
        check(value.Value == "written" && slot.Value == 14)
    }

    @TestAttribute fun sameTypeFieldDoesNotImplementTheInterfaceSlot() {
        val value = SameTypeField()
        val slot: IMutableValue<Int> = value
        check(value.Value == 17 && slot.Value == 19)
        value.Value = 18
        slot.Value = 20
        check(value.Value == 18 && slot.Value == 20)
    }

    @TestAttribute fun kotlinSubclassDoesNotGainFalseAbstractObligations() {
        val value = FieldSlotSubclass()
        value.Value = "derived"
        check(value.Value == "derived" && (value as IMutableValue<Int>).Value == 13)
    }

    @TestAttribute fun crossAssemblyGenericFieldKeepsConstructedOwnerArguments() {
        val value = GenericField<Int, String>(53, "slot")
        check(value.Value == 53 && (value as IValue<String>).Value == "slot")
        value.Value = 59
        check(value.Value == 59)
        val derived = GenericFieldSlotSubclass()
        check(derived.Value == "derived" && (derived as IValue<Int>).Value == 43)
    }

    @TestAttribute fun inheritedNullableFieldKeepsItsNullability() {
        val value = NullableField()
        check(value.Value == null)
        value.Value = "value"
        check(value.Value?.length == 5)
        value.Value = null
        check(value.Value == null && (value as IValue<Int>).Value == 23)
    }

    @TestAttribute fun readonlyFieldsRemainVisibleBesideSlots() {
        val direct = DirectReadonlyField()
        check(direct.Value == "readonly direct" && (direct as IValue<Int>).Value == 37)
        val inherited = ReadonlyField()
        check(inherited.Value == "readonly base" && (inherited as IValue<Int>).Value == 29)
    }

    @TestAttribute fun protectedFieldRetainsSubclassAccess() {
        val value = ProtectedFieldSlotSubclass()
        check(value.read() == "protected base")
        value.write("changed")
        check(value.read() == "changed" && (value as IValue<Int>).Value == 31)
    }

    @TestAttribute fun reimplementedInterfaceDoesNotOverwriteBaseField() {
        val value = FieldSlotReimplementation()
        check(value.Value == 47 && (value as IMutableValue<Int>).Value == 47)
        check((value as SameTypeField).Value == 17)
    }

    @TestAttribute fun staticFieldAndInstanceInterfaceSlotRemainSeparate() {
        StaticField.Value = "changed"
        check(StaticField.Value == "changed")
        check((StaticField() as IValue<Int>).Value == 41)
    }
}
