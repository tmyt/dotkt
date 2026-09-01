import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.clr.byref
import roundtrip.clrrefparameters.incrementReferenced
import roundtrip.clrrefparameters.incrementReferencedInline
import roundtrip.clrrefparameters.swapReferenced
import roundtrip.clrrefparameters.updateReferencedInline
import roundtrip.clrrefparameters.updateReferencedThroughSameModuleInline

class ClrRefParameterTests {
    @TestAttribute
    fun referencedKotlinManagedReferenceParametersRemainLive() {
        var value = 7
        assertEquals(12, incrementReferenced(byref(value), 5))
        assertEquals(12, value)
        assertEquals(14, incrementReferencedInline(byref(value), 2))
        assertEquals(14, value)
        assertEquals(42, updateReferencedInline(byref(value)) { it * 3 })
        assertEquals(42, value)
        assertEquals(46, updateReferencedThroughSameModuleInline(byref(value)))
        assertEquals(46, value)

        var first = "left"
        var second = "right"
        swapReferenced(byref(first), byref(second))
        assertEquals("right:left", "$first:$second")
    }
}
