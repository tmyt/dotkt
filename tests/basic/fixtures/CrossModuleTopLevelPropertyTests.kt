import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import kotlin.coroutines.intrinsics.COROUTINE_SUSPENDED

class CrossModuleTopLevelPropertyTests {
    @TestAttribute
    fun computedPropertyKeepsDeclaringFileOwner() {
        val first: Any = COROUTINE_SUSPENDED
        val second: Any = COROUTINE_SUSPENDED
        assertTrue(first === second)
        assertTrue(first === COROUTINE_SUSPENDED)
    }
}
