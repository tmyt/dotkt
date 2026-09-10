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

open class InheritedCarrierGenericBase<T>(private val item: T) : InheritedCarrierName<T> {
    override fun name(): T = item
}
open class InheritedCarrierGenericMiddle : InheritedCarrierGenericBase<String>("indirect")
class InheritedCarrierDirect<out T>(val value: T) : InheritedCarrierGenericBase<String>("direct")
class InheritedCarrierIndirect<out T>(val value: T) : InheritedCarrierGenericMiddle()
private fun inheritedCarrierBaseName(value: InheritedCarrierGenericBase<*>): Any? = value.name()
private fun <T> inheritedCarrierDirect(value: InheritedCarrierDirect<T>): String = inheritedCarrierName(value)
private fun <T> inheritedCarrierIndirect(value: InheritedCarrierIndirect<T>): String =
    "${inheritedCarrierName(value)}:${inheritedCarrierBaseName(value)}"

class InheritedCarrierInterfaceTests {
    @TestAttribute
    fun genericBasesPreserveClosedInterfacesAndAncestorCarriers() {
        val direct: InheritedCarrierDirect<Any> = InheritedCarrierDirect(7)
        val indirect: InheritedCarrierIndirect<Any> = InheritedCarrierIndirect("text")
        assertEquals("direct", inheritedCarrierDirect(direct))
        assertEquals("indirect:indirect", inheritedCarrierIndirect(indirect))
    }

    @TestAttribute
    fun concreteBaseInterfacesRemainContractsOfVariantValues() {
        assertEquals("42:local", inheritedCarrierForward(InheritedCarrierTask(7)))
        val widened: InheritedCarrierTask<Any> = InheritedCarrierTask("text")
        assertEquals("42:local", inheritedCarrierForward(widened))
    }
}
