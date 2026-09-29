package roundtriptests.innerconstruction

import NUnit.Framework.TestAttribute
import roundtrip.innerconstruction.*

class ImportedConstructionHost<A> {
    fun <B> check(own: A, value: B) {
        val child = ConstructionDerived<A, B>().Child(own, value)
        checkConstructedChild(child, own, value)
    }
}

class InnerConstructionFrameTests {
    @TestAttribute
    fun localConstructionKeepsDeclarationAndCallerFramesSeparate() {
        checkCapturedConstructionFrames()
        checkLocalConstruction(17, "outer")
        checkLocalConstruction("own", 23)
        checkLocalConstruction<Int?, String?>(null, null)
        ConstructionHost<Int>().check(29, "method")
        ConstructionHost<String>().check("class", 31)
        ConstructionHost<Int?>().check<String?>(null, null)
    }

    @TestAttribute
    fun importedConstructionPreservesOwnAndEnclosingFrames() {
        ImportedConstructionHost<Int>().check(47, "outer")
        ImportedConstructionHost<String>().check("own", 53)
        ImportedConstructionHost<Int?>().check<String?>(null, null)
    }
}
