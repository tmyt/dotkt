import NUnit.Framework.TestAttribute
import fieldprojection.FieldBox
import fieldprojection.FieldMiddle
import fieldprojection.CovariantFieldBox
import fieldprojection.ReadonlyFieldBox
import fieldprojection.RestrictedFieldBox
import fieldprojection.readField
import fieldprojection.writeRestrictedField

class ReferencedProjectedClrFieldTests {
    @TestAttribute fun referencedStarFieldReadsPreserveNativeInstantiations() {
        val text = FieldBox("reference")
        val number = FieldBox(42)
        val nullable = FieldBox<Int?>(null)
        val textView: FieldBox<*> = text
        val numberView: FieldBox<*> = number
        val nullableView: FieldBox<*> = nullable
        check(textView.value === text.value && readField(text) == "reference")
        check(textView === text && textView.value === text.value)
        check(numberView.value == 42 && readField(number) == 42)
        check(nullableView.value == null && readField(nullable) == null)
        val readonly: ReadonlyFieldBox<*> = ReadonlyFieldBox(43)
        check(readonly.value == 43)
    }

    @TestAttribute fun referencedProjectedWritesRemainVisibleThroughOtherViews() {
        val concrete = FieldBox<Any?>("before")
        val writer: FieldBox<in String> = concrete
        val reader: FieldBox<out Any?> = concrete
        writer.value = "after"
        check(concrete.value == "after" && reader.value == "after" && readField(concrete) == "after")
        val nullable = FieldBox<Int?>(null)
        val nullableWriter: FieldBox<in Int?> = nullable
        nullableWriter.value = 44
        check(readField(nullable) == 44)
        nullableWriter.value = null
        check(readField(nullable) == null)
        val restricted = RestrictedFieldBox<Any?>("before")
        writeRestrictedField(restricted, "after")
        val restrictedView: RestrictedFieldBox<*> = restricted
        check(restrictedView.value == "after")
    }

    @TestAttribute fun referencedDeclarationSiteVarianceReadsTheNativeField() {
        val exact = CovariantFieldBox("covariant")
        val wide: CovariantFieldBox<Any?> = exact
        val projected: CovariantFieldBox<*> = exact
        check(exact.value == "covariant" && wide.value == "covariant" && projected.value == "covariant")
        val number: CovariantFieldBox<Any?> = CovariantFieldBox(42)
        check(number.value == 42)
    }

    @TestAttribute fun inheritedFieldsKeepTheirActualDeclaringConstruction() {
        val concrete = FieldMiddle<Int, String>("base")
        val projected: FieldMiddle<*, *> = concrete
        check(projected.value == "base")
        val writable = FieldMiddle<Int, Any?>("before")
        val writer: FieldMiddle<*, in String> = writable
        writer.value = "after"
        check(writable.value == "after" && (writable as FieldBox<*>).value == "after")
    }
}
