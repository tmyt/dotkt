import NUnit.Framework.TestAttribute
import roundtrip.companionclassifiers.GenericOuter
import roundtrip.companionclassifiers.Outer
import roundtrip.companionclassifiers.answer
import roundtrip.companionclassifiers.directToken
import roundtrip.companionclassifiers.makeToken
import roundtrip.companionclassifiers.readToken

class CompanionOwnedClassifierTests {
    @TestAttribute
    fun namedAndDefaultCompanionClassifiersRoundTrip() {
        check(directToken() == 42)
        check(Outer.Token(42).value == 42)
        val token: Outer.Named.Token = makeToken()
        check(token.value == "named")
        check(readToken(Outer.Named.Token("consumer")) == "consumer")
        check(Outer.Named.WithFactory.Factory.answer() == 45)
        check(GenericOuter.Companion.Item("generic").value == "generic")
        check(GenericOuter.Companion.Item.Nested(46).value == 46)
        check(GenericOuter.Companion.Item.Key.create() == 44)
    }
}
