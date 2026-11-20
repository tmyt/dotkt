package roundtrip.consumer

import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import roundtrip.annotationproperties.ImportedLabel
import roundtrip.annotationproperties.ImportedArrays
import roundtrip.annotationproperties.producedLabel
import roundtrip.annotationproperties.producedArrays
import roundtrip.annotationproperties.ImportedTags
import roundtrip.annotationproperties.ImportedTypes
import roundtrip.annotationproperties.CallbackMarker
import roundtrip.annotationproperties.PreviewApi
import roundtrip.annotationproperties.PreviewMarker
import roundtrip.annotationproperties.producedTags
import roundtrip.annotationproperties.producedTypes
import roundtrip.annotationproperties.ProducerDefaultLabel
import roundtrip.kclassvalues.ImportedTyped

@ImportedLabel(code = 19)
@ImportedTyped
private class ConsumerDefaultLabel

@SubclassOptInRequired(PreviewMarker::class)
private interface PreviewSubApi : PreviewApi

@OptIn(PreviewMarker::class)
private class PreviewImpl : PreviewSubApi { override fun name() = "preview" }

private annotation class NestedLabel(val label: ImportedLabel = ImportedLabel())
private annotation class LabelArray(val code: Int = 7, vararg val labels: ImportedLabel)
@NestedLabel @LabelArray private class UnencodableLabels

class AnnotationPropertyRoundtripTests {
    @CallbackMarker
    private fun <@CallbackMarker T> invokeMarked(callback: @CallbackMarker () -> T): T = callback()

    @TestAttribute
    fun importedAnnotationRetainsItsKotlinMetaAnnotations() {
        val callback: @CallbackMarker () -> Int = { 17 }
        assertEquals(17, invokeMarked(callback))
        assertEquals("preview", PreviewImpl().name())
    }

    @TestAttribute
    fun scalarAnnotationPropertiesSurviveDllImport() {
        assertEquals("producer", producedLabel().value)
        assertEquals(17, producedLabel().code)
        assertEquals(11, ImportedLabel("consumer").code)
        fun applied(type: System.Type) = type.GetCustomAttributes(ImportedLabel::class as System.Type, false)[0] as ImportedLabel
        val producer = applied(ProducerDefaultLabel::class as System.Type)
        assertEquals("default", producer.value)
        assertEquals(17, producer.code)
        val type = ConsumerDefaultLabel::class as System.Type
        val consumer = applied(type)
        assertEquals("default", consumer.value)
        assertEquals(19, consumer.code)
        val typed = type.GetCustomAttributes(ImportedTyped::class as System.Type, false)[0] as ImportedTyped
        assertEquals(String::class, typed.type)
        val unsupported = UnencodableLabels::class as System.Type
        assertEquals(0, unsupported.GetCustomAttributes(NestedLabel::class as System.Type, false).size)
        assertEquals(0, unsupported.GetCustomAttributes(LabelArray::class as System.Type, false).size)
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
