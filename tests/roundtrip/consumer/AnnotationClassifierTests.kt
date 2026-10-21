package roundtrip.consumer

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.ObsoleteAttribute
import roundtrip.annotationclassifier.ImportedMarker
import roundtrip.annotationclassifier.ImportedManual
import roundtrip.annotationclassifier.ImportedExtended
import roundtrip.annotationclassifier.annotationIdentity
import roundtrip.annotationclassifier.makeAnnotation
import roundtrip.annotationclassifier.annotationMembership
import roundtrip.annotationclassifier.boundedIdentity
import roundtrip.annotationclassifier.reifiedMembership

class AnnotationClassifierRoundtripTests {
    @TestAttribute
    fun importedAnnotationSlotsRetainSourceTypesAndIdentity() {
        val marker = ImportedMarker("source")
        val value: Annotation = marker
        assertTrue(annotationIdentity(value) === marker)
        assertTrue(annotationMembership(marker))
        assertTrue((makeAnnotation() as ImportedMarker).label == "factory")
        val native = ObsoleteAttribute("native")
        assertTrue(annotationIdentity(native) === native)
        assertTrue(annotationMembership(native))
    }

    @TestAttribute
    fun importedManualImplementationsRetainTheirLogicalSupertypes() {
        val manual = ImportedManual()
        val extended = ImportedExtended()
        val first: Annotation = manual
        val second: Annotation = extended
        assertEquals("manual", manual.label)
        assertEquals("extended", extended.label)
        assertTrue(annotationIdentity(first) === manual)
        assertTrue(annotationIdentity(second) === extended)
        assertTrue(annotationMembership(first))
        assertTrue(annotationMembership(second))
    }

    @TestAttribute
    fun importedGenericAndReifiedHelpersUseLogicalMembership() {
        val marker = ImportedMarker("source")
        val manual = ImportedManual()
        val native = ObsoleteAttribute("native")
        assertTrue(reifiedMembership<Annotation>(marker))
        assertTrue(reifiedMembership<Annotation>(manual))
        assertTrue(reifiedMembership<Annotation>(native))
        assertEquals(false, reifiedMembership<Annotation>("other"))
        assertTrue(boundedIdentity(marker) === marker)
        assertTrue(boundedIdentity(manual) === manual)
        assertTrue(boundedIdentity(native) === native)
    }
}
