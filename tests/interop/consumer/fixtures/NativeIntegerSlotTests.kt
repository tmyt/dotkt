import NativeIntegerSlots.Counter
import NativeIntegerSlots.Driver
import NativeIntegerSlots.ICounter
import NUnit.Framework.TestAttribute
import System.Collections.Generic.Dictionary
import System.IntPtr
import System.UIntPtr

private class NativeIntegerCounter : Counter(), ICounter {
    override fun Signed(value: IntPtr): IntPtr = IntPtr(value.ToInt64() + 1L)
    override fun Unsigned(value: UIntPtr): UIntPtr = UIntPtr(value.ToUInt64() + 2uL)
    override fun Raw(value: IntPtr): IntPtr = IntPtr(value.ToInt64() + 3L)
}

class NativeIntegerSlotTests {
    @TestAttribute
    fun dispatchesThroughImportedInterfaceAndBaseClass() {
        val counter = NativeIntegerCounter()
        check(Driver.Check(counter, counter))
        val map = Dictionary<IntPtr, String?>()
        map[IntPtr(1)] = null
        check(Driver.AcceptNullValue(map))
    }
}
