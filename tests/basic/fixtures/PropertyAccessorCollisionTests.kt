import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

private var propertyAccessorTopStorage: Int = 1
var propertyAccessorTopValue: Int
    get() = propertyAccessorTopStorage
    set(value) { propertyAccessorTopStorage = value }
fun get_propertyAccessorTopValue(): Int = 20
fun set_propertyAccessorTopValue(value: Int) { propertyAccessorTopStorage = value + 100 }

class PropertyAccessorInstanceCollision {
    private var storage: Int = 2
    var value: Int
        get() = storage
        set(next) { storage = next }
    fun get_value(): Int = 30
    fun set_value(next: Int) { storage = next + 100 }
}

class PropertyAccessorCompanionObjectCollision {
    companion object {
        private var storage: Int = 3
        var value: Int
            get() = storage
            set(next) { storage = next }
        fun get_value(): Int = 40
        fun set_value(next: Int) { storage = next + 100 }
    }
}

class PropertyAccessorCompanionStaticCollision {
    companion {
        private var storage: Int = 4
        var value: Int
            get() = storage
            set(next) { storage = next }
        fun get_value(): Int = 50
        fun set_value(next: Int) { storage = next + 100 }
    }
}

class PropertyAccessorExtensionCollision(var storage: Int)
var PropertyAccessorExtensionCollision.value: Int
    get() = storage
    set(next) { storage = next }
fun PropertyAccessorExtensionCollision.get_value(): Int = 60
fun PropertyAccessorExtensionCollision.set_value(next: Int) { storage = next + 100 }

class PropertyAccessorCompanionExtensionCollision
private var propertyAccessorCompanionExtensionStorage: Int = 5
companion var PropertyAccessorCompanionExtensionCollision.value: Int
    get() = propertyAccessorCompanionExtensionStorage
    set(next) { propertyAccessorCompanionExtensionStorage = next }
companion fun PropertyAccessorCompanionExtensionCollision.get_value(): Int = 70
companion fun PropertyAccessorCompanionExtensionCollision.set_value(next: Int) {
    propertyAccessorCompanionExtensionStorage = next + 100
}

private interface StringItemProperty {
    val String.item: Int
}

private interface IntItemProperty {
    val Int.item: Int
}

private class OverloadedPropertyAccessorImpl : StringItemProperty, IntItemProperty {
    override val String.item: Int get() = length
    override val Int.item: Int get() = this + 100

    fun stringItem(value: String): Int = value.item
    fun intItem(value: Int): Int = value.item
}

private interface RenamedDefaultMethodSlot : Comparable<RenamedDefaultMethodSlot> {
    override fun compareTo(other: RenamedDefaultMethodSlot): Int = 81
}

private open class DefaultMethodPhysicalCollisionBase {
    fun CompareTo(other: RenamedDefaultMethodSlot): Int = 82
}

private open class DefaultMethodPhysicalCollision : DefaultMethodPhysicalCollisionBase(), RenamedDefaultMethodSlot
private class DerivedDefaultMethodPhysicalCollision : DefaultMethodPhysicalCollision() {
    override fun compareTo(other: RenamedDefaultMethodSlot): Int = 85
}

private interface GenericRenamedDefaultMethodSlot<T> : Comparable<GenericRenamedDefaultMethodSlot<T>> {
    override fun compareTo(other: GenericRenamedDefaultMethodSlot<T>): Int = 83
}

private open class GenericDefaultMethodPhysicalCollisionBase<T> {
    fun CompareTo(other: GenericRenamedDefaultMethodSlot<List<T>>): Int = 84
}

private class GenericDefaultMethodPhysicalCollision<T> :
    GenericDefaultMethodPhysicalCollisionBase<T>(), GenericRenamedDefaultMethodSlot<List<T>>

private interface NullableGenericInheritedDefault<T> {
    var value: T?
        get() = null
        set(next) {}

    fun echo(value: T?): T? = value
}

private class NullableGenericInheritedDefaultInt : NullableGenericInheritedDefault<Int>
private class NullableGenericInheritedDefaultString : NullableGenericInheritedDefault<String>

class PropertyAccessorCollisionTests {
    @TestAttribute
    fun propertiesAndSourceShapedFunctionsKeepDistinctPhysicalBodies() {
        propertyAccessorTopValue = 6
        assertEquals(6, propertyAccessorTopValue)
        assertEquals(20, get_propertyAccessorTopValue())
        set_propertyAccessorTopValue(7)
        assertEquals(107, propertyAccessorTopValue)

        val instance = PropertyAccessorInstanceCollision()
        instance.value = 8
        assertEquals(8, instance.value)
        assertEquals(30, instance.get_value())
        instance.set_value(9)
        assertEquals(109, instance.value)

        PropertyAccessorCompanionObjectCollision.value = 10
        assertEquals(10, PropertyAccessorCompanionObjectCollision.value)
        assertEquals(40, PropertyAccessorCompanionObjectCollision.get_value())
        PropertyAccessorCompanionObjectCollision.set_value(11)
        assertEquals(111, PropertyAccessorCompanionObjectCollision.value)

        PropertyAccessorCompanionStaticCollision.value = 12
        assertEquals(12, PropertyAccessorCompanionStaticCollision.value)
        assertEquals(50, PropertyAccessorCompanionStaticCollision.get_value())
        PropertyAccessorCompanionStaticCollision.set_value(13)
        assertEquals(113, PropertyAccessorCompanionStaticCollision.value)

        val extension = PropertyAccessorExtensionCollision(0)
        extension.value = 14
        assertEquals(14, extension.value)
        assertEquals(60, extension.get_value())
        extension.set_value(15)
        assertEquals(115, extension.value)

        PropertyAccessorCompanionExtensionCollision.value = 16
        assertEquals(16, PropertyAccessorCompanionExtensionCollision.value)
        assertEquals(70, PropertyAccessorCompanionExtensionCollision.get_value())
        PropertyAccessorCompanionExtensionCollision.set_value(17)
        assertEquals(117, PropertyAccessorCompanionExtensionCollision.value)

        val topProperty = ::propertyAccessorTopValue
        val topFunction = ::get_propertyAccessorTopValue
        assertEquals(107, topProperty.get())
        assertEquals(20, topFunction())
        val memberProperty = instance::value
        val memberFunction = instance::get_value
        assertEquals(109, memberProperty.get())
        assertEquals(30, memberFunction())

        val overloaded = OverloadedPropertyAccessorImpl()
        assertEquals(3, overloaded.stringItem("abc"))
        assertEquals(107, overloaded.intItem(7))

        val defaultCollision = DefaultMethodPhysicalCollision()
        assertEquals(81, (defaultCollision as RenamedDefaultMethodSlot).compareTo(defaultCollision))
        assertEquals(82, defaultCollision.CompareTo(defaultCollision))
        val derivedDefaultCollision = DerivedDefaultMethodPhysicalCollision()
        assertEquals(85, (derivedDefaultCollision as RenamedDefaultMethodSlot).compareTo(derivedDefaultCollision))

        val genericDefaultCollision = GenericDefaultMethodPhysicalCollision<String>()
        assertEquals(83, (genericDefaultCollision as GenericRenamedDefaultMethodSlot<List<String>>)
            .compareTo(genericDefaultCollision))
        assertEquals(84, genericDefaultCollision.CompareTo(genericDefaultCollision))

        val nullableInt: NullableGenericInheritedDefault<Int> = NullableGenericInheritedDefaultInt()
        nullableInt.value = 1
        assertEquals(null, nullableInt.value)
        assertEquals(42, nullableInt.echo(42))
        val nullableString: NullableGenericInheritedDefault<String> = NullableGenericInheritedDefaultString()
        nullableString.value = "ignored"
        assertEquals(null, nullableString.value)
        assertEquals("ok", nullableString.echo("ok"))

    }
}
