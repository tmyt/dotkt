package roundtriptests.constructorstararguments

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import roundtrip.constructorstararguments.ConstructorProjectionOwner

class ConstructorStarArgumentTests {
    @TestAttribute
    fun importedConstructorPreservesProjectedOuterArgument() {
        val exact = ConstructorProjectionOwner(42)
        val star: ConstructorProjectionOwner<*> = exact
        val token = star.Token("kept")
        assertTrue(token.value == "kept")
        assertTrue(token.outer() == 42)
        val projected: ConstructorProjectionOwner<out Any> = exact
        val projectedToken = projected.Token(17)
        assertTrue(projectedToken.value == 17)
        assertTrue(projectedToken.outer() == 42)
    }
}
