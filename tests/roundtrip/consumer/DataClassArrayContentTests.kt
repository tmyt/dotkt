import NUnit.Framework.TestAttribute
import roundtrip.arraycontent.ArrayRecord
import roundtrip.arraycontent.makeArrayRecord

class CrossModuleDataClassArrayContentTests {
    @TestAttribute
    fun generatedProducerMembersKeepSourceGenericFrames() {
        val text = makeArrayRecord(arrayOf("one", "two"))
        check(text.toString() == "ArrayRecord(values=[one, two], ints=[1, 2])")
        check(text.hashCode() == 31 * text.values.contentHashCode() + 994)
        val unsigned = makeArrayRecord(arrayOf(1u, UInt.MAX_VALUE))
        check(unsigned.toString() == "ArrayRecord(values=[1, 4294967295], ints=[1, 2])")
        check(unsigned.hashCode() == 31 * unsigned.values.contentHashCode() + 994)
    }

    @TestAttribute
    fun generatedProducerMembersKeepNullableElementAndArrayContent() {
        val value = ArrayRecord(arrayOf<String?>(null, "x"), null)
        check(value.toString() == "ArrayRecord(values=[null, x], ints=null)")
        check(value.hashCode() == 31 * value.values.contentHashCode())
    }
}
