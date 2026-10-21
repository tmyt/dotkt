import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.Attribute
import System.ObsoleteAttribute

private annotation class ClassifierAnnotation(val label: String)
private open class AnnotationOrdinaryBase(val label: String)
private class ManualAnnotation : AnnotationOrdinaryBase("manual"), Annotation
private interface ExtendedAnnotation : Annotation
private class ExtendedManualAnnotation : AnnotationOrdinaryBase("extended"), ExtendedAnnotation
private fun annotationMembership(value: Any?): Boolean = value is Annotation
private fun annotationIdentity(value: Annotation): Annotation = value
private fun <T : Annotation> boundedAnnotationIdentity(value: T): T = value
private inline fun <reified T> annotationReifiedIs(value: Any?): Boolean = value is T

class AnnotationClassifierTests {
    @TestAttribute
    fun authoredManualAndNativeValuesHaveLogicalMembership() {
        val authored: Any = ClassifierAnnotation("source")
        val manual: Any = ManualAnnotation()
        val native: Any = ObsoleteAttribute("native")
        assertTrue(annotationMembership(authored))
        assertTrue(annotationMembership(manual))
        assertTrue(annotationMembership(native))
        assertTrue(authored is Attribute)
        assertTrue(native is Attribute)
        assertEquals(false, manual is Attribute)
        assertEquals(false, annotationMembership("not an annotation"))
    }

    @TestAttribute
    fun castsPreserveIdentityAndEvaluateTheirOperandOnce() {
        val native: Any = ObsoleteAttribute("native")
        var calls = 0
        fun operand(): Any { calls++; return native }
        val cast = operand() as Annotation
        assertEquals(1, calls)
        assertTrue(cast === native)
        assertTrue(annotationIdentity(cast) === native)
        assertTrue((native as? Annotation) === native)
        val other: Any = "other"
        assertTrue((other as? Annotation) == null)
        var rejected = false
        try { other as Annotation } catch (e: ClassCastException) { rejected = true }
        assertTrue(rejected)
        val absent: Any? = null
        assertTrue(absent is Annotation?)
        assertTrue((absent as? Annotation) == null)
        var rejectedNull = false
        try { absent as Annotation } catch (e: NullPointerException) { rejectedNull = true }
        assertTrue(rejectedNull)
    }

    @TestAttribute
    fun reifiedChecksAndBoundsKeepBothRepresentations() {
        val authored = ClassifierAnnotation("source")
        val manual = ManualAnnotation()
        val native = ObsoleteAttribute("native")
        assertTrue(annotationReifiedIs<Annotation>(authored))
        assertTrue(annotationReifiedIs<Annotation>(manual))
        assertTrue(annotationReifiedIs<Annotation>(native))
        assertEquals(false, annotationReifiedIs<Annotation>("other"))
        assertTrue(annotationReifiedIs<Annotation?>(null))
        assertTrue(boundedAnnotationIdentity(authored) === authored)
        assertTrue(boundedAnnotationIdentity(manual) === manual)
        assertTrue(boundedAnnotationIdentity(native) === native)
    }

    @TestAttribute
    fun interfaceInheritanceDoesNotChangeTheOrdinaryClassBase() {
        val value = ExtendedManualAnnotation()
        val widened: Annotation = value
        assertEquals("extended", value.label)
        assertTrue(annotationMembership(widened))
        assertTrue(annotationIdentity(widened) === value)
        assertTrue((value as Any) is ExtendedAnnotation)
        assertEquals(false, (value as Any) is Attribute)
    }

    @TestAttribute
    fun arraysAndStdlibAnnotationsUseTheSameClassifier() {
        val native = ObsoleteAttribute("native")
        val manual = ManualAnnotation()
        val values = arrayOf<Annotation>(ClassifierAnnotation("source"), manual, native, Deprecated("stdlib"))
        for (value in values) assertTrue(annotationMembership(value))
        assertTrue(values[1] === manual)
        assertTrue(values[2] === native)
        assertTrue(annotationReifiedIs<Array<Annotation>>(values))
        assertTrue(annotationReifiedIs<Annotation>(Deprecated("stdlib")))
    }
}
