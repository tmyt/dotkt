import ExplicitMethodInterop.ExplicitOperations
import ExplicitMethodInterop.InheritedExplicitOperation
import ExplicitMethodInterop.IOperations
import ExplicitMethodInterop.StringTransformer
import ExplicitMethodInterop.IPropertySlot
import ExplicitMethodInterop.IFunctionSlot
import ExplicitMethodInterop.IPrivateDefaultPropertySlot
import ExplicitMethodInterop.IReadOnlyObjectPropertySlot
import ExplicitMethodInterop.IReadOnlyNominalPropertySlot
import ExplicitMethodInterop.PropertySlotBaseValue
import ExplicitMethodInterop.PropertySlotDerivedValue
import ExplicitMethodInterop.ReadOnlyNominalPropertyBase
import ExplicitMethodInterop.TwoArgumentPropertyBase
import ExplicitMethodInterop.IGenericValuePropertySlot
import ExplicitMethodInterop.IGenericFunctionSlot
import ExplicitMethodInterop.InheritedFunctionBase
import ExplicitMethodInterop.DerivedInheritedFunctionBase
import ExplicitMethodInterop.IInheritedPropertySlot
import ExplicitMethodInterop.IDerivedPropertySlot
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private class DerivedExplicitOperations : ExplicitOperations()

private class DistinctPropertyAndFunctionSlots : IPropertySlot, IFunctionSlot {
    override var value: Int = 1
    override fun get_value(): Int = 20
    override fun set_value(value: Int) { this.value = value + 100 }
}

private class PrivateDefaultPropertyAndFinalFunction : IPrivateDefaultPropertySlot {
    fun get_value(): Int = 99
    fun set_value(value: Int) {}
}

private class CovariantExternalPropertySlot : IReadOnlyObjectPropertySlot {
    override val value: String = "narrow"
}

private class CovariantExternalNominalPropertySlot : IReadOnlyNominalPropertySlot {
    override val value: PropertySlotDerivedValue = PropertySlotDerivedValue("nominal-narrow")
}

private class CovariantExternalNominalPropertyBase : ReadOnlyNominalPropertyBase() {
    override val value: PropertySlotDerivedValue = PropertySlotDerivedValue("nominal-base-narrow")
}

private class GenericExternalPropertyBase :
    TwoArgumentPropertyBase<String, PropertySlotDerivedValue>() {
    override val value: PropertySlotDerivedValue = PropertySlotDerivedValue("generic-arity-base")
}

// Deliberately declared before the local default-property interface: bridge allocation must not depend on source or
// input-file order.
private class DefaultExternalPropertyAndFunctionSlots : DefaultExternalPropertySlot, IFunctionSlot {
    override fun get_value(): Int = 50
    override fun set_value(value: Int) {}
}

private interface DefaultExternalPropertySlot : IPropertySlot {
    override var value: Int
        get() = 40
        set(value) { if (value < 0) throw IllegalArgumentException() }
}

private interface RedeclaredDefaultExternalPropertySlot : DefaultExternalPropertySlot {
    override var value: Int
        get() = 41
        set(value) { if (value < 0) throw IllegalArgumentException() }
}

private class RedeclaredDefaultExternalPropertyImplementation : RedeclaredDefaultExternalPropertySlot

private open class LocalInheritedFunctionBase {
    fun get_value(): Int = 60
    fun set_value(value: Int) {}
}

private class DefaultExternalPropertyAndLocalInheritedFunctionSlot :
    LocalInheritedFunctionBase(), DefaultExternalPropertySlot

private class DefaultExternalPropertyAndReferencedInheritedFunctionSlot :
    InheritedFunctionBase(), DefaultExternalPropertySlot

private class DefaultExternalPropertyAndDeepReferencedInheritedFunctionSlot :
    DerivedInheritedFunctionBase(), DefaultExternalPropertySlot

private interface DefaultCovariantExternalPropertySlot : IReadOnlyNominalPropertySlot {
    override val value: PropertySlotDerivedValue
        get() = PropertySlotDerivedValue("default-covariant-property")
}

// The interface default reaches IReadOnlyNominalPropertySlot through a signature-changing MethodImpl bridge
// (PropertySlotDerivedValue -> PropertySlotBaseValue). The unrelated source function occupies the external accessor's
// physical CLR signature, so the class must consume the frontend-selected default through that exact bridge.
private class DefaultCovariantExternalPropertyAndFunctionCollision : DefaultCovariantExternalPropertySlot {
    fun get_value(): PropertySlotBaseValue = PropertySlotBaseValue("default-covariant-function")
}

private interface DefaultGenericExternalPropertySlot<T> : IGenericValuePropertySlot<T> {
    fun propertyValue(): T
    override var value: T
        get() = propertyValue()
        set(value) { if (value == propertyValue()) return }
}

private class DefaultGenericExternalPropertyAndFunctionSlots :
    DefaultGenericExternalPropertySlot<String>, IGenericFunctionSlot<String> {
    override fun propertyValue(): String = "default-generic-property"
    override fun get_value(): String = "default-generic-function"
    override fun set_value(value: String) {}
}

private class GenericExternalPropertyAndFunctionSlots :
    IGenericValuePropertySlot<String>, IGenericFunctionSlot<String> {
    override var value: String = "property"
    override fun get_value(): String = "function"
    override fun set_value(value: String) { this.value = "function:$value" }
}

private class InheritedExternalPropertySlot : IDerivedPropertySlot {
    override val inheritedValue: Int = 73
}

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

    @TestAttribute
    fun externalPropertyAndOrdinaryFunctionSlotsMapIndependently() {
        val implementation = DistinctPropertyAndFunctionSlots()
        val property: IPropertySlot = implementation
        val function: IFunctionSlot = implementation
        property.value = 3
        assertEquals(3, property.value)
        assertEquals(20, function.get_value())
        function.set_value(4)
        assertEquals(104, property.value)
        assertEquals(20, implementation.get_value())
        assertEquals(104, implementation.value)

        val privateDefaultImplementation = PrivateDefaultPropertyAndFinalFunction()
        val privateDefaultProperty: IPropertySlot = privateDefaultImplementation
        assertEquals(42, privateDefaultProperty.value)
        privateDefaultProperty.value = 1
        assertEquals(42, privateDefaultProperty.value)
        assertEquals(99, privateDefaultImplementation.get_value())

        val covariant: IReadOnlyObjectPropertySlot = CovariantExternalPropertySlot()
        assertEquals("narrow", covariant.value)

        val nominalCovariant: IReadOnlyNominalPropertySlot = CovariantExternalNominalPropertySlot()
        assertEquals("nominal-narrow", nominalCovariant.value.Text)

        val nominalBaseCovariant: ReadOnlyNominalPropertyBase = CovariantExternalNominalPropertyBase()
        assertEquals("nominal-base-narrow", nominalBaseCovariant.value.Text)

        val genericBase: TwoArgumentPropertyBase<String, PropertySlotDerivedValue> =
            GenericExternalPropertyBase()
        assertEquals("generic-arity-base", genericBase.value.Text)

        val defaultImplementation = DefaultExternalPropertyAndFunctionSlots()
        val defaultProperty: IPropertySlot = defaultImplementation
        val defaultFunction: IFunctionSlot = defaultImplementation
        assertEquals(40, defaultProperty.value)
        defaultProperty.value = 1
        assertEquals(40, defaultProperty.value)
        assertEquals(50, defaultFunction.get_value())

        val redeclaredDefaultProperty: IPropertySlot = RedeclaredDefaultExternalPropertyImplementation()
        assertEquals(41, redeclaredDefaultProperty.value)
        redeclaredDefaultProperty.value = 1
        assertEquals(41, redeclaredDefaultProperty.value)

        val localInheritedProperty: IPropertySlot = DefaultExternalPropertyAndLocalInheritedFunctionSlot()
        assertEquals(40, localInheritedProperty.value)
        localInheritedProperty.value = 1
        assertEquals(40, localInheritedProperty.value)

        val referencedInheritedProperty: IPropertySlot = DefaultExternalPropertyAndReferencedInheritedFunctionSlot()
        assertEquals(40, referencedInheritedProperty.value)
        referencedInheritedProperty.value = 1
        assertEquals(40, referencedInheritedProperty.value)

        val deepReferencedInheritedProperty: IPropertySlot =
            DefaultExternalPropertyAndDeepReferencedInheritedFunctionSlot()
        assertEquals(40, deepReferencedInheritedProperty.value)
        deepReferencedInheritedProperty.value = 1
        assertEquals(40, deepReferencedInheritedProperty.value)

        val defaultCovariantImplementation = DefaultCovariantExternalPropertyAndFunctionCollision()
        val defaultCovariantProperty: IReadOnlyNominalPropertySlot = defaultCovariantImplementation
        assertEquals("default-covariant-property", defaultCovariantProperty.value.Text)
        assertEquals("default-covariant-function", defaultCovariantImplementation.get_value().Text)

        val defaultGenericImplementation = DefaultGenericExternalPropertyAndFunctionSlots()
        val defaultGenericProperty: IGenericValuePropertySlot<String> = defaultGenericImplementation
        val defaultGenericFunction: IGenericFunctionSlot<String> = defaultGenericImplementation
        assertEquals("default-generic-property", defaultGenericProperty.value)
        defaultGenericProperty.value = "ignored"
        assertEquals("default-generic-property", defaultGenericProperty.value)
        assertEquals("default-generic-function", defaultGenericFunction.get_value())

        val genericImplementation = GenericExternalPropertyAndFunctionSlots()
        val genericProperty: IGenericValuePropertySlot<String> = genericImplementation
        val genericFunction: IGenericFunctionSlot<String> = genericImplementation
        genericProperty.value = "changed"
        assertEquals("changed", genericProperty.value)
        assertEquals("function", genericFunction.get_value())
        genericFunction.set_value("changed")
        assertEquals("function:changed", genericProperty.value)

        val inheritedProperty: IInheritedPropertySlot = InheritedExternalPropertySlot()
        assertEquals(73, inheritedProperty.inheritedValue)

    }
}
