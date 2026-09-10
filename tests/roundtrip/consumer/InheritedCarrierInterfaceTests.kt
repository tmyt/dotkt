import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import inheritedcarrier.Middle
import inheritedcarrier.RunnableContract
import inheritedcarrier.NameContract

class ReferencedCarrierTask<out T>(val value: T) : Middle()
private fun referencedCarrierRun(value: RunnableContract): Int = value.run()
private fun referencedCarrierName(value: NameContract<String>): String = value.name()
private fun <T> referencedCarrierForward(value: ReferencedCarrierTask<T>): String =
    "${referencedCarrierRun(value)}:${referencedCarrierName(value)}"

class InheritedCarrierRoundtripTests {
    @TestAttribute
    fun referencedBaseInterfacesRemainContractsOfVariantValues() {
        assertEquals("43:referenced", referencedCarrierForward(ReferencedCarrierTask(7)))
        val widened: ReferencedCarrierTask<Any> = ReferencedCarrierTask("text")
        assertEquals("43:referenced", referencedCarrierForward(widened))
    }
}
