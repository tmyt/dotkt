import NUnit.Framework.TestAttribute
import NearestProjectedMembers.*

private class ProtectedFieldConsumer : ProtectedFieldMiddle() {
    fun read(): String = Value
    fun write(value: String) { Value = value }
}

class NearestProjectedMemberTests {
    @TestAttribute fun nearerFieldWinsForReadAndWrite() {
        val value = FieldMiddle()
        check(value.Value == "field")
        value.Value = "written"
        check(value.Value == "written")
        check((value as PropertyBase).Value == 7)
    }

    @TestAttribute fun inheritedFieldWinsOverFartherProperty() {
        val value = FieldLeaf()
        check(value.Value == "field")
        value.Value = "leaf"
        check(value.Value == "leaf")
    }

    @TestAttribute fun nearerPropertyStillWinsOverBaseField() {
        val value = PropertyLeaf()
        check(value.Value == 9)
        value.Value = 17
        check(value.Value == 17)
        check((value as FieldBase).Value == "base field")
    }

    @TestAttribute fun inheritedGenericFieldKeepsConstructedType() {
        val value = GenericFieldLeaf()
        check(value.Value == "generic")
        value.Value = "changed"
        check(value.Value == "changed")
        val number = GenericFieldMiddle(19)
        check(number.Value == 19)
        number.Value = 23
        check(number.Value == 23)
    }

    @TestAttribute fun staticFieldHidingPropertyKeepsStaticAccess() {
        StaticFieldMiddle.Value = "static field"
        check(StaticFieldMiddle.Value == "static field")
        StaticFieldMiddle.Value = "changed"
        check(StaticFieldMiddle.Value == "changed")
        check(StaticPropertyBase.Value == 7)
    }

    @TestAttribute fun staticPropertyHidingFieldKeepsItsAccessor() {
        StaticPropertyMiddle.Value = 19
        check(StaticPropertyMiddle.Value == 19)
        check(StaticFieldBase.Value == "base static field")
        PrivateStaticGetter.Value = 23
        check(PrivateStaticGetter.Read() == 23)
    }

    @TestAttribute fun mixedStaticAndInstanceHidingUsesSelectedDeclaration() {
        MixedStaticField.Value = "changed"
        check(MixedStaticField.Value == "changed")
        val field = MixedInstanceField()
        check(field.Value == "mixed instance field")
        field.Value = "written"
        check(field.Value == "written")
        MixedStaticProperty.Value = 17
        check(MixedStaticProperty.Value == 17)
        val property = MixedInstanceProperty()
        check(property.Value == 13)
        property.Value = 19
        check(property.Value == 19)
    }

    @TestAttribute fun protectedFieldWinsInsideKotlinSubclass() {
        val value = ProtectedFieldConsumer()
        check(value.read() == "protected")
        value.write("changed")
        check(value.read() == "changed")
    }
}
