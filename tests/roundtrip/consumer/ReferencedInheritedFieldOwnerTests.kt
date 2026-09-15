import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import fieldowner.FieldBase
import fieldowner.FieldMiddle

class ReferencedInheritedFieldOwnerTests {
    @TestAttribute
    fun referencedGenericFieldKeepsItsDeclaringFrame() {
        fun <T> exercise(value: T, next: T) {
            val instance = object : FieldMiddle<Int, T>(value) {
                fun read(): T = this.value
                fun write(next: T) { this.value = next }
                fun capture(): T = value
            }
            assertEquals(value, instance.read())
            instance.write(next)
            assertEquals(next, instance.read())
            assertEquals(next, (instance as FieldBase<T>).value)
            assertEquals(value, instance.capture())
        }
        exercise("old", "new")
        exercise(1, 2)
    }

    @TestAttribute
    fun referencedLateinitChecksAndUpdatesBaseStorage() {
        fun exercise(text: Int) {
            val instance = object : FieldMiddle<Int, String>("base") {
                fun read(): String = this.text
                fun write(next: String) { this.text = next }
                fun capture(): Int = text
            }
            var threw = false
            try { instance.read() } catch (e: Exception) {
                threw = e.message == "lateinit property text has not been initialized"
            }
            assertTrue(threw)
            instance.write("initialized")
            assertEquals("initialized", instance.read())
            assertEquals("initialized", (instance as FieldBase<String>).text)
            assertEquals(7, instance.capture())
        }
        exercise(7)
    }
}
