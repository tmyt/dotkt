package roundtriptests.constructedmethods

import NUnit.Framework.TestAttribute
import roundtrip.constructedmethods.*

fun <A, B, M> checkImportedConstructedMethods(outer: A, own: B, input: M) {
    val owner = MethodOwner(outer)
    check(owner.echo(input) == input)
    check(owner.unpack(owner.pack(input)) == input)
    val child = owner.Inner(own)
    check(child.echo(input) == input)
    check(child.unpack(child.pack(input)) == input)
    check(child.enclosing(input) == outer && child.inner(input) == own)
}

class ConstructedMethodResultTests {
    @TestAttribute
    fun importedMethodSpecsRetainOwnerAndMethodFrames() {
        checkImportedConstructedMethods(17, "own", true)
        checkImportedConstructedMethods("outer", 19, "result")
        checkImportedConstructedMethods<String?, Int?, Boolean?>(null, null, null)
        checkImportedConstructedMethods<String?, Int?, Boolean?>("outer", 23, true)
    }

    @TestAttribute
    fun localMethodSpecsRetainOwnerAndMethodFrames() {
        checkLocalConstructedMethods(29, "own", true)
        checkLocalConstructedMethods("outer", 31, "result")
        checkLocalConstructedMethods<String?, Int?, Boolean?>(null, null, null)
    }
}
