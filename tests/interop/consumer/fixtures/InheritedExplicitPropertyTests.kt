import NUnit.Framework.TestAttribute
import InheritedPropertyInterop.*

private class InheritedCountSubclass : IterableClassifierStorage.SetAndDictionary()
private class InheritedGenericPropertySubclass : GenericPropertyMix<String, Int>("public", "slot")
private class InheritedDifferentPropertySubclass : DifferentPropertyMix()
private class InheritedPropertyReimplementation : MutablePropertyMix<Int>(7, 17), IWritableValue<Int> {
    override var Value: Int = 27
}

class InheritedExplicitPropertyTests {
    @TestAttribute fun classCountUsesThePublicBaseSlot() {
        val value = IterableClassifierStorage.SetAndDictionary()
        value.Add(7)
        check(value.Count == 1)
    }

    @TestAttribute fun kotlinSubclassKeepsThePublicBaseCount() {
        val value = InheritedCountSubclass()
        value.Add(7)
        check(value.Count == 1)
    }

    @TestAttribute fun inheritedGenericPublicAndExplicitPropertiesStayIndependent() {
        val value = GenericPropertyMix<String, Int>("public", "slot")
        check(value.Value == "public")
        check((value as IValue<String>).Value == "slot")
        val derived = InheritedGenericPropertySubclass()
        check(derived.Value == "public")
        check((derived as IValue<String>).Value == "slot")
    }

    @TestAttribute fun differentPropertyTypesDoNotHideThePublicBase() {
        val value = InheritedDifferentPropertySubclass()
        check(value.Value == 7)
        check((value as IValue<String>).Value == "slot")
    }

    @TestAttribute fun inheritedPropertyAndExplicitSetterStayIndependent() {
        val value = MutablePropertyMix<Int>(7, 17)
        value.Value = 8
        check(value.Value == 8)
        val slot: IWritableValue<Int> = value
        check(slot.Value == 17)
        slot.Value = 18
        check(slot.Value == 18 && value.Value == 8)
    }

    @TestAttribute fun reimplementationDoesNotReplaceTheNonvirtualBaseProperty() {
        val value = InheritedPropertyReimplementation()
        check(value.Value == 27)
        check((value as IWritableValue<Int>).Value == 27)
        check((value as PropertyBase<Int>).Value == 7)
    }
}
