import NUnit.Framework.TestAttribute
import roundtrip.charsequencesupertypes.*

class CharSequenceSupertypeTests {
    @TestAttribute
    fun importedInterfaceRetainsKotlinCharSequenceArgument() {
        val slot: Slot<CharSequence> = Text()
        check(slot.read().toString() == "ok")
        check(Text().read().toString() == "ok")
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
        check(DerivedText().value.toString() == "base")
    }

    @TestAttribute
    fun overlappingErasurePreservesSourceEdgeAndUnchangedSibling() {
        val source = MixedText()
        val pair: PairSlot<CharSequence, Int?> = source
        val marker: Marker<String> = source
        check(pair.left().toString() == "mixed")
        check(pair.right() == null)
        check(marker === source)
    }
}
