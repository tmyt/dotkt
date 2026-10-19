package roundtriptests.inheritedarguments

import NUnit.Framework.TestAttribute
import roundtrip.inheritedarguments.ArgumentBox
import roundtrip.inheritedarguments.ArgumentChild
import roundtrip.inheritedarguments.localArgumentForward

private fun <Unused, T> importedArgumentForward(child: ArgumentChild<Unused, T>, value: ArgumentBox<T>): T =
    child.accept(value)

class InheritedDeclarationArgumentTests {
    @TestAttribute
    fun importedInheritedArgumentsUseTheirSelectedDeclarationFrame() {
        val strings = ArgumentChild<Int, String>()
        check(strings.accept(ArgumentBox("value")) == "value")
        check(strings.acceptDefault(ArgumentBox("default")) == "default")
        check(strings.choose(ArgumentBox("owner"), 42) == 42)
        check(importedArgumentForward(strings, ArgumentBox("open")) == "open")
        check(localArgumentForward(strings, ArgumentBox("local")) == "local")
        check(with(strings) { ArgumentBox("extension").memberAccept() } == "extension")
        val integers = ArgumentChild<String, Int>()
        check(integers.accept(ArgumentBox(42)) == 42)
        check(integers.acceptDefault(ArgumentBox(7), "provided") == 7)
        check(importedArgumentForward(integers, ArgumentBox(9)) == 9)
    }
}
