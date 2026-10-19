package roundtrip.consumer

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.annotationproperties.ImportedLabel
import roundtrip.annotationproperties.ImportedArrays
import roundtrip.annotationproperties.producedLabel
import roundtrip.annotationproperties.producedArrays
import roundtrip.annotationproperties.ImportedTags
import roundtrip.annotationproperties.ImportedTypes
import roundtrip.annotationproperties.producedTags
import roundtrip.annotationproperties.producedTypes

class AnnotationPropertyRoundtripTests {
    @TestAttribute
    fun scalarAnnotationPropertiesSurviveDllImport() {
        assertEquals("producer", producedLabel().value)
        assertEquals(17, producedLabel().code)
        assertEquals(11, ImportedLabel("consumer").code)
    }

    @TestAttribute
    fun arrayAnnotationPropertiesSurviveDllImport() {
        val produced = producedArrays()
        assertEquals("two", produced.names[1])
        assertEquals(9, produced.counts[1])
        val local = ImportedArrays(arrayOf("consumer"), intArrayOf(19))
        assertEquals("consumer", local.names[0])
        assertEquals(19, local.counts[0])
        assertEquals("two", producedTags().tags[1])
        assertEquals("consumer", ImportedTags("consumer").tags[0])
    }

    @TestAttribute
    fun annotationAndEnumPropertiesSurviveDllImport() {
        val produced = producedTypes()
        assertEquals("nested", produced.nested.value)
        assertEquals(DeprecationLevel.WARNING, produced.kind)
        val local = ImportedTypes(ImportedLabel("consumer"), DeprecationLevel.ERROR)
        assertEquals("consumer", local.nested.value)
        assertEquals(DeprecationLevel.ERROR, local.kind)
    }

    @TestAttribute
    fun importedAnnotationPropertyReferenceUsesItsSelectedDeclaration() {
        val getter: (ImportedLabel) -> String = ImportedLabel::value
        assertEquals("producer", getter(producedLabel()))
    }
}
