import NUnit.Framework.TestAttribute
import kotlin.clr.ClrField

class ProjectedFieldBox<T>(seed: T) { @ClrField var value: T = seed }
class ProjectedReadonlyFieldBox<T>(seed: T) { @ClrField val value: T = seed }
private class ProjectedMixedFieldBox<T>(seed: T) {
    @ClrField var raw: T = seed
    var reads: Int = 0
    var writes: Int = 0
    var computed: T
        get() { reads++; return raw }
        set(value) { writes++; raw = value }
}

private var projectedFieldOrder = ""
private val projectedFieldOrderBox = ProjectedFieldBox<Any?>("initial")
private fun projectedFieldReadReceiver(): ProjectedFieldBox<*> {
    projectedFieldOrder += "r"
    return projectedFieldOrderBox
}
private fun projectedFieldWriteReceiver(): ProjectedFieldBox<in String> {
    projectedFieldOrder += "r"
    return projectedFieldOrderBox
}
private fun projectedFieldWriteValue(): String {
    projectedFieldOrder += "v"
    return "changed"
}

class ProjectedClrFieldTests {
    @TestAttribute fun starFieldReadsPreserveReferenceValueAndNull() {
        val text = ProjectedFieldBox("reference")
        val number = ProjectedFieldBox(42)
        val nullable = ProjectedFieldBox<Int?>(null)
        val textView: ProjectedFieldBox<*> = text
        val numberView: ProjectedFieldBox<*> = number
        val nullableView: ProjectedFieldBox<*> = nullable
        check(textView === text && textView.value === text.value)
        check(numberView.value == 42 && nullableView.value == null)
        nullable.value = 43
        check(nullableView.value == 43)
        val readonly: ProjectedReadonlyFieldBox<*> = ProjectedReadonlyFieldBox(44)
        check(readonly.value == 44)
    }

    @TestAttribute fun legalOutReadsAndInWritesUseTheSameStorage() {
        val native = ProjectedFieldBox<Any?>("before")
        val writer: ProjectedFieldBox<in String> = native
        writer.value = "after"
        val reader: ProjectedFieldBox<out Any?> = native
        check(native.value == "after" && reader.value == "after")
        val nullable = ProjectedFieldBox<Int?>(null)
        val nullableWriter: ProjectedFieldBox<in Int?> = nullable
        nullableWriter.value = 42
        val nullableReader: ProjectedFieldBox<out Int?> = nullable
        check(nullableReader.value == 42)
        nullableWriter.value = null
        check(nullableReader.value == null)
    }

    @TestAttribute fun projectedFieldEvaluationIsOnceAndInSourceOrder() {
        projectedFieldOrder = ""
        projectedFieldWriteReceiver().value = projectedFieldWriteValue()
        check(projectedFieldOrder == "rv" && projectedFieldOrderBox.value == "changed")
        projectedFieldOrder = ""
        check(projectedFieldReadReceiver().value == "changed")
        check(projectedFieldOrder == "r")
    }

    @TestAttribute fun directStorageDoesNotSelectACustomPropertyAccessor() {
        val concrete = ProjectedMixedFieldBox<Any?>("first")
        val reader: ProjectedMixedFieldBox<*> = concrete
        check(reader.raw == "first" && concrete.reads == 0)
        check(reader.computed == "first" && concrete.reads == 1)
        val writer: ProjectedMixedFieldBox<in String> = concrete
        writer.raw = "raw"
        check(concrete.raw == "raw" && concrete.writes == 0)
        writer.computed = "property"
        check(concrete.raw == "property" && concrete.writes == 1)
    }
}
