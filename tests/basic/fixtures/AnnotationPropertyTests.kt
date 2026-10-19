import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import kotlin.clr.ClrField

annotation class UserAnnotationLabel(val value: String, val code: Int = 7)
annotation class UserAnnotationArrays(val names: Array<String>, val counts: IntArray)
annotation class UserFieldAnnotation(@ClrField val value: String)

class AnnotationPropertyTests {
    @TestAttribute
    fun scalarPropertiesKeepTheirSourceGetterIdentity() {
        val label = UserAnnotationLabel("value")
        assertEquals("value", label.value)
        assertEquals(7, label.code)
        assertEquals(13, UserAnnotationLabel("explicit", 13).code)
    }

    @TestAttribute
    fun arrayPropertiesKeepTheirSourceElementTypes() {
        val label = UserAnnotationArrays(arrayOf("first", "second"), intArrayOf(3, 5))
        assertEquals(2, label.names.size)
        assertEquals("second", label.names[1])
        assertEquals(2, label.counts.size)
        assertEquals(5, label.counts[1])
    }

    @TestAttribute
    fun propertyReferencesBindTheAnnotationAccessor() {
        val getter: (UserAnnotationLabel) -> String = UserAnnotationLabel::value
        assertEquals("referenced", getter(UserAnnotationLabel("referenced")))
    }

    @TestAttribute
    fun explicitClrFieldKeepsItsFieldAccess() {
        assertEquals("field", UserFieldAnnotation("field").value)
    }
}
