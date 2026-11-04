import NUnit.Framework.TestAttribute
import roundtrip.companionclassifiers.GenericOuter
import roundtrip.companionclassifiers.Outer
import roundtrip.companionclassifiers.answer
import roundtrip.companionclassifiers.directToken
import roundtrip.companionclassifiers.makeLeaf
import roundtrip.companionclassifiers.makeToken
import roundtrip.companionclassifiers.readLeaf
import roundtrip.companionclassifiers.readToken
import roundtrip.companionclassifiers.tokens

class CompanionOwnedClassifierTests {
    @TestAttribute
    fun namedAndDefaultCompanionClassifiersRoundTrip() {
        check(directToken() == 42)
        check(Outer.Token(42).value == 42)
        val token: Outer.Named.Token = makeToken()
        check(token.value == "named")
        check(readToken(Outer.Named.Token("consumer")) == "consumer")
        check(tokens().single().value == "named")
        check(Outer.Named.WithFactory.Factory.answer() == 45)
        check(Outer().readPrivate() == 49)
        check(Outer().readProtected() == 53)
        check(Outer.Sibling().readPrivate() == 50)
        check(Outer.cachedPrivate == 51)
        check(GenericOuter<String>().readPrivate() == 52)
        check(GenericOuter.Companion.Item("generic").value == "generic")
        check(GenericOuter.Companion.Item.Nested(46).value == 46)
        val leaf: GenericOuter.Companion.Item.Nested.Companion.Leaf = makeLeaf()
        check(leaf.value == 47)
        check(readLeaf(GenericOuter.Companion.Item.Nested.Companion.Leaf(48)) == 48)
        check(GenericOuter.Companion.Item.Key.create() == 44)
    }
}
