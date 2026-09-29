package roundtriptests.constructedmethods

import NUnit.Framework.TestAttribute
import roundtrip.constructedmethods.*
import kotlin.clr.byref

fun <A> checkConcreteMethodArguments(outer: A) {
    val owner = MethodOwner(outer)
    check(owner.echo(5) == 5)
    check(owner.echo<Int?>(7) == 7)
    check(owner.echo<Any>(11) == 11)
    check(owner.pack(13).value == 13)
    val child = owner.Inner("own")
    check(child.echo<Int?>(null) == null)
    check(child.echo<Any>(17) == 17)
    var value = 19
    check(child.replace(byref(value), 23) == 19 && value == 23)
    var text: String? = null
    check(child.replace(byref(text), "updated") == null && text == "updated")
}

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
        checkConcreteMethodArguments("owner")
        checkConcreteMethodArguments(31)
    }

    @TestAttribute
    fun localMethodSpecsRetainOwnerAndMethodFrames() {
        checkLocalConstructedMethods(29, "own", true)
        checkLocalConstructedMethods("outer", 31, "result")
        checkLocalConstructedMethods<String?, Int?, Boolean?>(null, null, null)
    }
}
