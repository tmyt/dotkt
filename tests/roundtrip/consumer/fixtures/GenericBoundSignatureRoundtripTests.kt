import NUnit.Framework.TestAttribute
import roundtrip.genericboundsignature.BoundSink
import roundtrip.genericboundsignature.StringBoundSink
import roundtrip.genericboundsignature.selectedBound

class GenericBoundSignatureRoundtripTests {
    @TestAttribute
    fun differentSourceBoundsKeepCrossDllOverloadSelection() {
        val collection = mutableListOf<String>()
        val sink = StringBoundSink()
        check(selectedBound(collection, "one") == "collection")
        check(selectedBound(sink, "two") == "sink")
        check(collection.single() == "one")
        check(sink.latest == "two")
    }

    @TestAttribute
    fun differentSourceBoundsKeepCrossDllCallableReferenceSelection() {
        val collection = mutableListOf<String>()
        val sink = StringBoundSink()
        val collectionCall: (MutableList<String>, String) -> String = ::selectedBound
        val sinkCall: (BoundSink<String>, String) -> String = ::selectedBound
        check(collectionCall(collection, "three") == "collection")
        check(sinkCall(sink, "four") == "sink")
        check(collection.single() == "three")
        check(sink.latest == "four")
    }
}
