import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert
import roundtrip.memberextensionsurface.GenericMemberPropertyCarrier
import roundtrip.memberextensionsurface.GenericMemberPropertyHost

private class GenericMemberPropertyConsumer : GenericMemberPropertyHost() {
    inline fun <reified T> matches(value: Any?): Boolean =
        GenericMemberPropertyCarrier<T>(value).memberMatches

    fun <T> ordinaryValue(value: Any?): T? =
        GenericMemberPropertyCarrier<T>(value).ordinaryMemberValue

    fun <T> updateOrdinaryValue(carrier: GenericMemberPropertyCarrier<T>, value: T?) {
        carrier.ordinaryMemberValue = value
    }

    fun <T> copyOrdinaryValue(
        source: GenericMemberPropertyCarrier<T>,
        target: GenericMemberPropertyCarrier<T>,
    ) {
        target.ordinaryMemberValue = source.ordinaryMemberValue
    }

    fun <T> incrementOrdinaryCount(carrier: GenericMemberPropertyCarrier<T>) {
        carrier.ordinaryMemberCount++
    }

    fun <T> addOrdinaryCount(carrier: GenericMemberPropertyCarrier<T>, delta: Int) {
        carrier.ordinaryMemberCount += delta
    }

    fun <T> directOrdinaryValue(
        host: GenericMemberPropertyHost,
        carrier: GenericMemberPropertyCarrier<T>,
    ): T? = with(host) { carrier.ordinaryMemberValue }
}

class MemberExtensionPropertyTests {
    @TestAttribute
    fun genericMemberExtensionPropertiesSurviveDllKlibRoundtrip() {
        val consumer = GenericMemberPropertyConsumer()
        ClassicAssert.IsTrue(consumer.matches<String?>(null))
        ClassicAssert.IsFalse(consumer.matches<String>(null))
        ClassicAssert.AreEqual("ok", consumer.ordinaryValue<String>("ok"))
        ClassicAssert.AreEqual(42, consumer.ordinaryValue<Int>(42))
        val carrier = GenericMemberPropertyCarrier<String>("before")
        consumer.updateOrdinaryValue(carrier, "after")
        ClassicAssert.AreEqual("after", consumer.ordinaryValue<String>(carrier.value))
        val copied = GenericMemberPropertyCarrier<String>("before")
        consumer.copyOrdinaryValue(carrier, copied)
        ClassicAssert.AreEqual("after", consumer.ordinaryValue<String>(copied.value))
        val counted = GenericMemberPropertyCarrier<Unit>(41)
        consumer.incrementOrdinaryCount(counted)
        ClassicAssert.AreEqual(42, counted.value)
        consumer.addOrdinaryCount(counted, 8)
        ClassicAssert.AreEqual(50, counted.value)
        ClassicAssert.AreEqual("direct", consumer.directOrdinaryValue(
            GenericMemberPropertyHost(), GenericMemberPropertyCarrier<String>("direct")))
    }
}
