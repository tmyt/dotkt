package roundtrip.enumcollision

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

enum class RichLocalState(val code: Int) { CURRENT(101), LATER(103) }
enum class BasicLocalState { YES, NO }

class PrivateRichEnumCollisionTests {
    @TestAttribute
    fun unusedPrivateEnumsDoNotBlockIndependentLibrariesOrPublishedInlineBodies() {
        assertEquals(10, producerAState())
        assertEquals(30, producerBState())
        assertEquals("a0", producerADefault())
        assertEquals("b1", producerBDefault())
        assertEquals(73, publishedEnumCode { it })
    }

    @TestAttribute
    fun localBasicAndRichDeclarationsOwnTheirEnumOperations() {
        assertEquals(listOf(RichLocalState.CURRENT, RichLocalState.LATER), enumValues<RichLocalState>().toList())
        assertEquals(RichLocalState.LATER, enumValueOf<RichLocalState>("LATER"))
        assertEquals(RichLocalState.CURRENT, RichLocalState.valueOf("CURRENT"))
        assertEquals(103, RichLocalState.LATER.code)
        assertEquals(listOf(RichLocalState.CURRENT, RichLocalState.LATER), RichLocalState.entries.toList())
        assertEquals(listOf(BasicLocalState.YES, BasicLocalState.NO), enumValues<BasicLocalState>().toList())
        assertEquals(BasicLocalState.NO, enumValueOf<BasicLocalState>("NO"))
        assertEquals("NO", BasicLocalState.NO.name)
        assertEquals(1, BasicLocalState.NO.ordinal)
    }
}
