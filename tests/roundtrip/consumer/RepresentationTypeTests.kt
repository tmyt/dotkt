package roundtriptests.representationtypes

import NUnit.Framework.TestAttribute
import roundtrip.representationtypes.*

class RepresentationTypeTests {
    @TestAttribute
    fun ordinaryArgumentsReturnsAndAuthoredSupertypesKeepKotlinClassifiers() {
        val custom = RepresentationText("abc")
        check(representationAt("abc", 1) == 'b')
        check(representationAt(custom, 1) == 'b')
        check(echoRepresentation(custom) === custom)
        check(echoRepresentation("abc").toString() == "abc")
        check(nullableRepresentation(null) == null)
        check(nullableRepresentation(custom) === custom)
        check(custom.length == 3 && custom.toString() == "rendered")
    }

    @TestAttribute
    fun genericFramesAndNativeNominalTypesRemainExact() {
        val custom = RepresentationText("abc")
        check(genericRepresentation(42, custom) == 42)
        check(genericRepresentation("value", "abc") == "value")
        check(boundedRepresentation(custom) === custom)
        val box = RepresentationBox(custom)
        check(box.value === custom)
        check(boxedRepresentation(box) === custom)
        val native = System.Version(1, 2)
        check(nativeRepresentation(native) === native)
        check(nativeRepresentation(native).Major == 1)
        val receiver = RepresentationReceiver(17)
        var evaluations = 0
        fun textOnce(): String {
            evaluations++
            return "abc"
        }
        check(receiver.select(textOnce(), "picked") == "picked")
        check(evaluations == 1)
        check(receiver.select(custom, 42) == 42)
        check(receiver.echo(custom) === custom)
        check(receiver.select(textOnce(), native) === native)
        check(evaluations == 2 && receiver.value == 17)
    }
}
