import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

private fun foundationalSum(a: Int, b: Int): Int = a + b
private fun foundationalFizz(n: Int): String = if (n == 0) "zero" else "n=$n"

class FoundationalControlFlowTests {
    @TestAttribute
    fun functionsConditionalsAndWhileLoop() {
        assertEquals(5, foundationalSum(2, 3))
        val values = mutableListOf<String>()
        var i = 0
        while (i < 3) {
            values.add(foundationalFizz(i))
            i++
        }
        assertEquals("zero|n=1|n=2", values.joinToString("|"))
    }
}
