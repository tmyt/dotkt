import NUnit.Framework.TestAttribute
import roundtrip.charsequencesupertypes.*

class CharSequenceSupertypeTests {
    @TestAttribute
    fun importedInterfaceRetainsKotlinCharSequenceArgument() {
        val slot: Slot<CharSequence> = Text()
        check(slot.read().toString() == "ok")
        val unchanged: Slot<String> = UnchangedText()
        check(unchanged.read() == "unchanged")
    }

    @TestAttribute
    fun importedNullableSupertypeRetainsNullabilityAndKotlinIdentity() {
        val slot: Slot<CharSequence?> = NullableText()
        check(slot.read() == null)
    }

    @TestAttribute
    fun importedNestedSupertypeRetainsInnerKotlinIdentity() {
        val slot: Slot<Slot<CharSequence>> = NestedText()
        check(slot.read().read().toString() == "ok")
    }

    @TestAttribute
    fun importedBaseRetainsKotlinCharSequenceArgument() {
        val base: Base<CharSequence> = DerivedText()
        check(base.value.toString() == "base")
    }
}
