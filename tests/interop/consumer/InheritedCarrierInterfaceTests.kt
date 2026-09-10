import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import InheritedCarrierInterop.Middle
import InheritedCarrierInterop.IValue
import InheritedCarrierInterop.IStamp

class ClrInheritedCarrierTask<out T>(val value: T) : Middle()
private fun clrInheritedCarrierRead(value: IValue<String>): String = value.Read()
private fun clrInheritedCarrierStamp(value: IStamp): Int = value.Stamp
private fun <T> clrInheritedCarrierForward(value: ClrInheritedCarrierTask<T>): String =
    "${clrInheritedCarrierStamp(value)}:${clrInheritedCarrierRead(value)}"

class ClrInheritedCarrierInterfaceTests {
    @TestAttribute
    fun explicitImplementationsFromClrBaseFillInheritedContracts() {
        assertEquals("44:clr", clrInheritedCarrierForward(ClrInheritedCarrierTask(7)))
        val widened: ClrInheritedCarrierTask<Any> = ClrInheritedCarrierTask("text")
        assertEquals("44:clr", clrInheritedCarrierForward(widened))
    }
}
