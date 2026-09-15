import ByRefLikeInterop.ByRefLikeApi
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.clr.ClrRef
import kotlin.clr.byref

class InlineSharedLocalTests {
    @TestAttribute
    fun reassignedReadOnlySpanStaysLocal() {
        var span = ByRefLikeApi.Chars("abc")
        var total = 0
        run { total += span.Length }
        span = ByRefLikeApi.Chars("d")
        run { total += span.Length }
        assertEquals(4, total)
    }

    @TestAttribute
    fun reassignedUserRefStructStaysLocal() {
        var tally = ByRefLikeApi.MakeTally(3)
        var total = 0
        run { total += ByRefLikeApi.ReadTally(tally) }
        tally = ByRefLikeApi.MakeTally(7)
        run { total += ByRefLikeApi.ReadTally(tally) }
        assertEquals(10, total)
    }

    @TestAttribute
    fun inlineWritesAndNestedInitializersShareLocals() {
        var first = 1
        run {
            var second = first
            run { second += first }
            first = second
        }
        assertEquals(2, first)
        var addressed = 3
        run { incrementInlineSharedLocal(byref(addressed)) }
        assertEquals(8, addressed)
    }
}

private fun incrementInlineSharedLocal(slot: ClrRef<Int>) { slot.value += 5 }
