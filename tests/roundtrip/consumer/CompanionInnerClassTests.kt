import NUnit.Framework.TestAttribute
import roundtrip.companioninner.AnnotationOwner
import roundtrip.companioninner.EnumOwner
import roundtrip.companioninner.GenericOwner
import roundtrip.companioninner.InterfaceOwner
import roundtrip.companioninner.NamedOwner
import roundtrip.companioninner.ObjectOwner
import roundtrip.companioninner.makeCapture

class CompanionInnerClassTests {
    @TestAttribute
    fun innerDeclarationsBelowAllClassifierOwnersRoundTrip() {
        val named = NamedOwner.Named.Value(17)
        check(named.read() == 17)
        check(named.Capture().read() == 17)
        check(NamedOwner.Named.Value(23).Capture().read() == 23)
        check(NamedOwner.Named.DeclarationOnly("consumer").Capture().read() == "consumer")

        val generic = GenericOwner.Companion.Value("outer")
        check(generic.read() == "outer")
        val capture = generic.Capture(29)
        check(capture.outer() == "outer")
        check(capture.own() == 29)
        check(capture.Leaf().outer() == "outer")
        check(capture.Leaf().middle() == 29)
        val imported: GenericOwner.Companion.Value<String>.Capture<Int> = makeCapture()
        check(imported.outer() == "producer")
        check(imported.own() == 42)
        check(imported.Leaf().middle() == 42)

        check(ObjectOwner.Value(31).read() == 31)
        check(ObjectOwner.Value(37).Capture().read() == 37)
        check(InterfaceOwner.Companion.Value(41).read() == 41)
        check(InterfaceOwner.Companion.Value(43).Capture().read() == 43)
        check(InterfaceOwner.DirectValue(67).read() == 67)
        check(InterfaceOwner.DirectValue(71).Capture().read() == 71)
        check(EnumOwner.Value(47).read() == 47)
        check(EnumOwner.Value(53).Capture().read() == 53)
        check(EnumOwner.ITEM.Tag().label() == "ITEM0")
        check(AnnotationOwner.Value(59).read() == 59)
        check(AnnotationOwner.Value(61).Capture().read() == 61)
    }
}
