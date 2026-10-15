import kotlin.concurrent.Volatile
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse
import kotlin.reflect.KMutableProperty0

class LateinitStateBox<T : Any> {
    lateinit var value: T
    fun initialized(): Boolean = this::value.isInitialized
    fun other(other: LateinitStateBox<T>): Boolean = other::value.isInitialized
    fun otherStar(other: LateinitStateBox<*>): Boolean = other::value.isInitialized
    fun supplied(factory: () -> LateinitStateBox<T>): Boolean = factory()::value.isInitialized
}

class LateinitStateOuter<T : Any> {
    private lateinit var value: T
    fun reference(): KMutableProperty0<T> = this::value
    inner class Inner { fun initialized(): Boolean = this@LateinitStateOuter::value.isInitialized }
    fun callback(): () -> Boolean = { this::value.isInitialized }
}

private lateinit var lateinitStateTop: String
private fun lateinitStateTopInitialized(): Boolean = ::lateinitStateTop.isInitialized
object LateinitStateObject {
    lateinit var value: String
    fun initialized(): Boolean = ::value.isInitialized
}
class LateinitStateStatic<T> {
    companion {
        lateinit var value: String
        fun initialized(): Boolean = ::value.isInitialized
    }
}
class LateinitStateCompanion {
    companion object {
        lateinit var value: String
        fun initialized(): Boolean = ::value.isInitialized
    }
}
open class LateinitStateBase {
    open lateinit var value: String
    fun initialized(): Boolean = this::value.isInitialized
}
class LateinitStateDerived : LateinitStateBase() {
    override var value: String
        get() = "override"
        set(value) { }
    fun initializeBase() { super.value = "base" }
}
interface LateinitStateContract { var value: String }
class LateinitStateImplementation : LateinitStateContract {
    override lateinit var value: String
    fun initialized(): Boolean = this::value.isInitialized
}
class LateinitStateVolatile {
    @Volatile lateinit var value: String
    fun initialized(): Boolean = this::value.isInitialized
}

class LateinitInitializationTests {
    @TestAttribute
    fun genericDefaultValuesAndUninitializedReads() {
        val number = LateinitStateBox<Int>()
        assertFalse(number.initialized())
        assertFalse(number.otherStar(number))
        var message = ""
        try { number.value } catch (error: Exception) { message = error.message ?: "" }
        assertEquals("lateinit property value has not been initialized", message)
        number.value = 0
        assertTrue(number.initialized())
        assertTrue(number.otherStar(number))
        assertEquals(0, number.value)
        number.value = 42
        assertEquals(42, number.value)
        val text = LateinitStateBox<String>()
        assertFalse(text.initialized())
        assertFalse(number.otherStar(text))
        text.value = ""
        assertTrue(text.initialized())
        assertTrue(number.otherStar(text))
        assertEquals("", text.value)
    }

    @TestAttribute
    fun referencesOtherInstancesAndSingleReceiverEvaluation() {
        val first = LateinitStateBox<Int>()
        val second = LateinitStateBox<Int>()
        var evaluations = 0
        assertFalse(first.supplied { evaluations++; second })
        assertEquals(1, evaluations)
        val bound = first::value
        bound.set(0)
        assertEquals(0, bound.get())
        assertTrue(first.other(first))
        assertFalse(first.other(second))
        val unbound = LateinitStateBox<Int>::value
        unbound.set(second, 7)
        assertEquals(7, unbound.get(second))
        assertTrue(first.supplied { evaluations++; second })
        assertEquals(2, evaluations)
        assertEquals(0, first.value)
    }

    @TestAttribute
    fun privateStorageAcrossNestedAndCapturedQueries() {
        val outer = LateinitStateOuter<Int>()
        val inner = outer.Inner()
        val callback = outer.callback()
        val reference = outer.reference()
        assertFalse(inner.initialized())
        assertFalse(callback())
        reference.set(0)
        assertTrue(inner.initialized())
        assertTrue(callback())
        assertEquals(0, reference.get())
    }

    @TestAttribute
    fun topLevelObjectAndCompanionStorage() {
        assertFalse(lateinitStateTopInitialized())
        lateinitStateTop = "top"
        assertTrue(lateinitStateTopInitialized())
        assertEquals("top", lateinitStateTop)
        assertFalse(LateinitStateObject.initialized())
        LateinitStateObject.value = "object"
        assertTrue(LateinitStateObject.initialized())
        assertFalse(LateinitStateStatic.initialized())
        LateinitStateStatic.value = "static"
        assertTrue(LateinitStateStatic.initialized())
        assertEquals("static", LateinitStateStatic.value)
        assertFalse(LateinitStateCompanion.initialized())
        LateinitStateCompanion.value = "companion"
        assertTrue(LateinitStateCompanion.initialized())
    }

    @TestAttribute
    fun queryDoesNotDispatchToAnOverriddenGetter() {
        val derived = LateinitStateDerived()
        assertEquals("override", derived.value)
        assertFalse(derived.initialized())
        derived.initializeBase()
        assertTrue(derived.initialized())
        assertEquals("override", derived.value)
        val implementation = LateinitStateImplementation()
        assertFalse(implementation.initialized())
        val contract: LateinitStateContract = implementation
        contract.value = "interface"
        assertTrue(implementation.initialized())
        assertEquals("interface", contract.value)
    }

    @TestAttribute
    fun volatileAndFailedAssignmentPreserveInitializationState() {
        val value = LateinitStateVolatile()
        assertFalse(value.initialized())
        fun fail(): String = throw IllegalStateException("assignment")
        try { value.value = fail() } catch (error: Exception) { }
        assertFalse(value.initialized())
        value.value = "assigned"
        assertTrue(value.initialized())
        assertEquals("assigned", value.value)
    }
}
