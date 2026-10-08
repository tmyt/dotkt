import NativeArrayResultFrames.Owner
import NativeArrayResultFrames.Boundary
import NUnit.Framework.TestAttribute

private fun <A, B> checkNativeArrayResultFrames(first: A, second: B) {
    val methodArray = Boundary.Create<A>(first)
    val owner = Owner<B>(second)
    val echoed = owner.Echo<A>(methodArray)
    check(Boundary.Same<A>(methodArray, echoed))
    check(Boundary.Element<A>(echoed) == first)
    val read = owner.Read()
    val property = owner.Values
    check(Boundary.Same<B>(read, property))
    check(Boundary.Element<B>(read) == second)
    check(Boundary.Element<B>(property) == second)
}

class NativeArrayResultFrameTests {
    @TestAttribute
    fun exactNativeArrayResultsPreserveDistinctOwnerAndCallerFrames() {
        checkNativeArrayResultFrames("method-reference", 42)
        checkNativeArrayResultFrames(19, "owner-reference")
        checkNativeArrayResultFrames("method-reference", "owner-reference")
    }
}
