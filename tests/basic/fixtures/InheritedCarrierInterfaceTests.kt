import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

interface InheritedCarrierRunnable { fun run(): Int }
interface InheritedCarrierName<T> { fun name(): T }
abstract class InheritedCarrierBase : InheritedCarrierRunnable
abstract class InheritedCarrierMiddle : InheritedCarrierBase(), InheritedCarrierName<String> {
    override fun name(): String = "local"
}
class InheritedCarrierTask<out T>(val value: T) : InheritedCarrierMiddle() {
    override fun run(): Int = 42
}

private fun inheritedCarrierRun(value: InheritedCarrierRunnable): Int = value.run()
private fun inheritedCarrierName(value: InheritedCarrierName<String>): String = value.name()
private fun <T> inheritedCarrierForward(value: InheritedCarrierTask<T>): String =
    "${inheritedCarrierRun(value)}:${inheritedCarrierName(value)}"

class InheritedCarrierInterfaceTests {
    @TestAttribute
    fun concreteBaseInterfacesRemainContractsOfVariantValues() {
        assertEquals("42:local", inheritedCarrierForward(InheritedCarrierTask(7)))
        val widened: InheritedCarrierTask<Any> = InheritedCarrierTask("text")
        assertEquals("42:local", inheritedCarrierForward(widened))
    }
}
