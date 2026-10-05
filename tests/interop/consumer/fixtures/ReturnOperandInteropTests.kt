import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.clr.byref
import ReturnOperands.Counter
import ReturnOperands.References

class ReturnOperandInteropTests {
    private fun bump(flag: Boolean): Int {
        var counter = Counter(0)
        counter.Bump(if (flag) return 7 else 1)
        return counter.Count
    }

    private fun write(flag: Boolean): Int {
        var value = 0
        References.Write(byref(value), if (flag) return 7 else 3)
        return value
    }

    @TestAttribute
    fun earlyReturnsPreserveForeignReceiverAndReferenceLocations() {
        assertEquals(1, bump(false))
        assertEquals(7, bump(true))
        assertEquals(3, write(false))
        assertEquals(7, write(true))
    }

    @TestAttribute
    fun tryValuesPreserveForeignReceiverAndReferenceLocations() {
        var counter = Counter(0)
        counter.Bump(try { 2 } catch (e: Exception) { 4 })
        assertEquals(2, counter.Count)
        var value = 0
        References.Write(byref(value), try { 3 } catch (e: Exception) { 4 })
        assertEquals(3, value)
    }
}
