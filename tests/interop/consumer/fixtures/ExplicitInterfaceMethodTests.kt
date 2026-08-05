import ExplicitMethodInterop.ExplicitOperations
import ExplicitMethodInterop.InheritedExplicitOperation
import ExplicitMethodInterop.IOperations
import ExplicitMethodInterop.StringTransformer
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private class DerivedExplicitOperations : ExplicitOperations()

class ExplicitInterfaceMethodTests {
    @TestAttribute
    fun concreteClassSurfaceUsesExplicitInterfaceSlots() {
        val operations = ExplicitOperations()
        assertEquals(12, operations.Compute(2))
        assertEquals("value!", operations.Compute("value"))
        assertEquals("generic", operations.Echo("generic"))
        assertEquals("explicit", operations.Name)
    }

    @TestAttribute
    fun derivedClassHasNoFictionalAbstractObligation() {
        val operations = DerivedExplicitOperations()
        assertEquals(15, operations.Compute(5))
        assertEquals("derived", operations.Echo("derived"))

        val throughInterface: IOperations = operations
        assertEquals("slot!", throughInterface.Compute("slot"))
    }

    @TestAttribute
    fun inheritedAndConstructedInterfaceSlotsAreSurfaced() {
        assertEquals(23, InheritedExplicitOperation().BaseCompute(3))
        assertEquals("generic?", StringTransformer().Transform("generic"))
    }
}
