import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import inheritedcarrier.Middle
import inheritedcarrier.RunnableContract
import inheritedcarrier.NameContract
import inheritedcarrier.GenericBase
import inheritedcarrier.GenericMiddle
import inheritedcarrier.UnitMiddle
import inheritedcarrier.UnitContract
import inheritedcarrier.genericBaseName

class ReferencedCarrierTask<out T>(val value: T) : Middle()
class ReferencedCarrierRedeclared<out T>(val value: T) : Middle(), NameContract<String>
class ReferencedCarrierDirect<out T>(val value: T) : GenericBase<String>("direct")
class ReferencedCarrierIndirect<out T>(val value: T) : GenericMiddle()
class ReferencedCarrierUnit<out T>(val value: T) : UnitMiddle()
private fun referencedCarrierRun(value: RunnableContract): Int = value.run()
private fun referencedCarrierName(value: NameContract<String>): String = value.name()
private fun <T> referencedCarrierForward(value: ReferencedCarrierTask<T>): String =
    "${referencedCarrierRun(value)}:${referencedCarrierName(value)}"
private fun <T> referencedCarrierRedeclared(value: ReferencedCarrierRedeclared<T>): String = referencedCarrierName(value)
private fun <T> referencedCarrierDirect(value: ReferencedCarrierDirect<T>): String = referencedCarrierName(value)
private fun <T> referencedCarrierIndirect(value: ReferencedCarrierIndirect<T>): String =
    "${referencedCarrierName(value)}:${genericBaseName(value)}"
private fun referencedCarrierUnitStamp(value: UnitContract<Unit>): Int = value.stamp()
private fun <T> referencedCarrierUnit(value: ReferencedCarrierUnit<T>): Int = referencedCarrierUnitStamp(value)

class InheritedCarrierRoundtripTests {
    @TestAttribute
    fun genericBasesPreserveClosedInterfacesAndAncestorCarriers() {
        val direct: ReferencedCarrierDirect<Any> = ReferencedCarrierDirect(7)
        val indirect: ReferencedCarrierIndirect<Any> = ReferencedCarrierIndirect("text")
        assertEquals("direct", referencedCarrierDirect(direct))
        assertEquals("indirect:indirect", referencedCarrierIndirect(indirect))
        assertEquals("referenced", referencedCarrierRedeclared(ReferencedCarrierRedeclared(7)))
    }

    @TestAttribute
    fun unitRemainsAValueTypeArgumentInInheritedInterfaces() {
        assertEquals(45, referencedCarrierUnit(ReferencedCarrierUnit(7)))
    }

    @TestAttribute
    fun referencedBaseInterfacesRemainContractsOfVariantValues() {
        assertEquals("43:referenced", referencedCarrierForward(ReferencedCarrierTask(7)))
        val widened: ReferencedCarrierTask<Any> = ReferencedCarrierTask("text")
        assertEquals("43:referenced", referencedCarrierForward(widened))
    }
}
