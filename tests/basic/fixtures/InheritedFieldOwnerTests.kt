import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import kotlin.clr.ClrField

open class InheritedLateinitBase(seed: String) {
    lateinit var value: String
    init { value = "base:" + seed }
}

open class InheritedPlainBase<T>(seed: T) {
    @ClrField var value: T = seed
}
open class InheritedFieldMiddle<A, B>(seed: B) : InheritedPlainBase<B>(seed)
open class InheritedUninitializedBase { lateinit var value: String }

class InheritedFieldOwnerTests {
    @TestAttribute
    fun boundAndUnboundReferencesUseInheritedStorage() {
        fun exercise(value: String) {
            class Local : InheritedUninitializedBase() { fun capture(): String = value }
            val instance = Local()
            val unbound = Local::value
            val bound = instance::value
            var threw = false
            try { unbound.get(instance) } catch (e: Exception) {
                threw = e.message == "lateinit property value has not been initialized"
            }
            assertTrue(threw)
            unbound.set(instance, "first")
            assertEquals("first", bound.get())
            bound.set("second")
            assertEquals("second", unbound.get(instance))
            assertEquals("second", (instance as InheritedUninitializedBase).value)
            assertEquals("captured", instance.capture())
        }
        exercise("captured")
    }

    @TestAttribute
    fun nullableReceiversKeepTheirNonNullFieldOwnerAfterChecks() {
        fun read(instance: InheritedFieldMiddle<Int, String>?): String? {
            if (instance != null) return instance.value
            return null
        }
        fun safeRead(instance: InheritedFieldMiddle<Int, String>?): String? = instance?.value
        assertEquals("text", read(InheritedFieldMiddle("text")))
        assertEquals("text", safeRead(InheritedFieldMiddle("text")))
        assertEquals(null, read(null))
        assertEquals(null, safeRead(null))
    }

    @TestAttribute
    fun boundReceiversKeepTheSelectedBaseField() {
        fun <T, U : InheritedFieldMiddle<Int, T>> readBound(instance: U): T = instance.value
        val text = InheritedFieldMiddle<Int, String>("text")
        val number = InheritedFieldMiddle<Int, Int>(42)
        assertEquals("text", readBound(text))
        assertEquals(42, readBound(number))
    }

    @TestAttribute
    fun anonymousCaptureDoesNotReplaceInheritedLateinit() {
        fun make(value: String) {
            val instance = object : InheritedLateinitBase(value) {
                fun read(): String = this.value
                fun write(next: String) { this.value = next }
                fun capture(): String = value
            }
            assertEquals("base:x", instance.read())
            instance.write("updated")
            assertEquals("updated", instance.read())
            assertEquals("updated", (instance as InheritedLateinitBase).value)
            assertEquals("x", instance.capture())
        }
        make("x")
    }

    @TestAttribute
    fun differentTypedCaptureDoesNotChangeInheritedFieldSignature() {
        fun make(value: Int) {
            val instance = object : InheritedLateinitBase(value.toString()) {
                fun read(): String = this.value
                fun write(next: String) { this.value = next }
                fun capture(): Int = value
            }
            assertEquals("base:7", instance.read())
            instance.write("updated")
            assertEquals("updated", instance.read())
            assertEquals(7, instance.capture())
        }
        make(7)
    }

    @TestAttribute
    fun localClassUsesDeclaringGenericFieldOwner() {
        fun make(value: String) {
            class Local : InheritedPlainBase<String>("base") {
                fun read(): String = this.value
                fun write(next: String) { this.value = next }
                fun capture(): String = value
            }
            val instance = Local()
            assertEquals("base", instance.read())
            instance.write("updated")
            assertEquals("updated", (instance as InheritedPlainBase<String>).value)
            assertEquals("x", instance.capture())
        }
        make("x")
    }

    @TestAttribute
    fun transitiveGenericOwnerUsesBaseArgumentsNotReceiverArguments() {
        fun <T> exercise(value: T, next: T) {
            val instance = object : InheritedFieldMiddle<Int, T>(value) {
                fun read(): T = this.value
                fun write(next: T) { this.value = next }
                fun capture(): T = value
            }
            assertEquals(value, instance.read())
            instance.write(next)
            assertEquals(next, instance.read())
            assertEquals(next, (instance as InheritedPlainBase<T>).value)
            assertEquals(value, instance.capture())
        }
        exercise("text", "changed")
        exercise(42, 73)
    }
}
