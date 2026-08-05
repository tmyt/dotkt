import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.wide.acceptWidened
import roundtrip.wide.narrowSource
import roundtrip.wide.param17
import roundtrip.wide.ret17
import roundtrip.wide.retAction17

class WideFunctionTypeTests {
    @TestAttribute
    fun wideFunctionValuesCrossTheModuleBoundary() {
        assertEquals(18, param17 { p1, _, _, _, _, _, _, _, _, _, _, _, _, _, _, _, p17 -> p1 + p17 })
        assertEquals(18, ret17()(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17))

        var seen = 0
        retAction17 { seen = it }(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17)
        assertEquals(18, seen)
    }

    @TestAttribute
    fun wideFunctionVarianceMatchesKotlinFunctionTypes() {
        val source: (Any, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> String = narrowSource()
        val widened: (String, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int, Int) -> Any = source
        assertEquals("s17", widened("s", 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17))
        assertEquals("s17", acceptWidened(source))
    }
}
