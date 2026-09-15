import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

open class BackingShadowBase<T>(private val value: T) {
    fun baseValue(): T = value
    open fun capturedValue(): T = value
}

inline fun backingShadowInline(value: String): BackingShadowBase<String> =
    object : BackingShadowBase<String>(value) {}

private fun <T> backingShadowAnonymous(value: T): BackingShadowBase<T> =
    object : BackingShadowBase<T>(value) {
        override fun capturedValue(): T = value
    }

class BackingShadowPlain(value: String) : BackingShadowBase<String>("base") {
    lateinit var value: String
    init { this.value = value }
}

open class BackingShadowPropertyBase(open var value: String)
class BackingShadowPropertyDerived : BackingShadowPropertyBase("base") {
    override var value: String = "derived"
    fun inheritedValue(): String = super.value
}

object BackingShadowSingleton : BackingShadowBase<String>("base") {
    val INSTANCE: String = "property"
}

class BackingFieldShadowTests {
    @TestAttribute
    fun inlineAnonymousConstructorKeepsItsOwnCaptureField() {
        assertEquals("first", backingShadowInline("first").baseValue())
        assertEquals("second", backingShadowInline("second").baseValue())
    }

    @TestAttribute
    fun genericAnonymousCaptureAndBaseStorageRemainDistinct() {
        val text = backingShadowAnonymous("text")
        val number = backingShadowAnonymous(42)
        assertEquals("text", text.baseValue())
        assertEquals("text", text.capturedValue())
        assertEquals(42, number.baseValue())
        assertEquals(42, number.capturedValue())
    }

    @TestAttribute
    fun localMutableCaptureDoesNotOverwriteBaseStorage() {
        var value = "initial"
        class Local : BackingShadowBase<String>(value) {
            override fun capturedValue(): String = value
            fun update(next: String) { value = next }
        }
        val instance = Local()
        value = "changed"
        assertEquals("initial", instance.baseValue())
        assertEquals("changed", instance.capturedValue())
        instance.update("updated")
        assertEquals("updated", value)
        assertEquals("updated", instance.capturedValue())
        assertEquals("initial", instance.baseValue())
    }

    @TestAttribute
    fun unrenamedLateinitFieldShadowsPrivateBaseStorage() {
        val instance = BackingShadowPlain("own")
        assertEquals("base", instance.baseValue())
        assertEquals("own", instance.value)
        instance.value = "updated"
        assertEquals("updated", instance.value)
        assertEquals("base", instance.baseValue())
    }

    @TestAttribute
    fun renamedOverridesAndSingletonStorageKeepTheirOwnIdentities() {
        val instance = BackingShadowPropertyDerived()
        instance.value = "updated"
        assertEquals("updated", instance.value)
        assertEquals("base", instance.inheritedValue())
        assertEquals("base", BackingShadowSingleton.baseValue())
        assertEquals("property", BackingShadowSingleton.INSTANCE)
    }
}
