package roundtriptests.innerfactorysignatures

import NUnit.Framework.TestAttribute
import roundtrip.innerfactorysignatures.FactoryOwner

private open class Middle<T>(value: T) : FactoryOwner<T>(value)
private class Leaf<T>(value: T) : Middle<T>(value)
private fun integerEntry(owner: Leaf<String>): String = owner.Entry(64).render()
private fun stringEntry(owner: Leaf<String>): String = owner.Entry("text").render()
private fun <T, E> genericEntry(owner: Leaf<T>, item: E): String = owner.GenericEntry(item).render()
private fun <T> defaultEntry(owner: Leaf<T>): String = owner.DefaultEntry().render()
private fun <T> defaultOuter(owner: Leaf<T>): T = owner.DefaultEntry().outerValue()

class InnerFactorySignatureTests {
    @TestAttribute fun inheritedOverloadsRetainTheirSelectedDeclaration() {
        val owner = Leaf("outer")
        check(integerEntry(owner) == "outer/int:64")
        check(stringEntry(owner) == "outer/string:text")
    }

    @TestAttribute fun erasedOuterUsesItsActualConstruction() {
        @Suppress("UNCHECKED_CAST")
        val owner = Leaf(7) as Leaf<String>
        check(integerEntry(owner) == "7/int:64")
        check(stringEntry(owner) == "7/string:text")
    }

    @TestAttribute fun genericAndDefaultInnerConstructorsKeepTheirFrames() {
        val integer = genericEntry(Leaf("outer"), 17)
        check(integer == "outer/17") { "generic Int: $integer" }
        val text = genericEntry(Leaf(23), "text")
        check(text == "23/text") { "generic String: $text" }
        val default = defaultEntry(Leaf("outer"))
        check(default == "outer/default") { "default: $default" }
        check(defaultOuter(Leaf<Int?>(null)) == null)
        check(defaultOuter(Leaf(29)) == 29)
    }
}
