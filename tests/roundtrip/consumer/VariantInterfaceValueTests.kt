import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import variantinterfacevalues.*

class ConsumerVariantSource : Source<Int> { override val item: Int get() = 23 }
class ConsumerVariantSink : Sink<Any> {
    var last = ""
    override fun accept(value: Any) { last = value.toString() }
}
fun <T> importedVariantCallback(values: Array<out Source<T>>, prefix: String): String =
    transform(values) { prefix + it.read().toString() }
fun <T> importedVariantReference(values: Array<out Source<T>>): String = transform(values, ::text)

class VariantInterfaceRoundtripTests {
    @TestAttribute fun importedCovariantParametersResultsAndProperties() {
        assertEquals("17", text<Any>(IntSource()))
        assertEquals("producer", text<Any>(TextSource()))
        assertEquals(18, read<Int>(IntSource()) + 1)
        assertEquals("17", source().item.toString())
        assertEquals("17", ValueFromSource<Any>(IntSource()).value.toString())
    }
    @TestAttribute fun importedContravariantArgument() {
        val sink = AnySink()
        write<Int>(sink, 31)
        assertEquals("31", sink.last)
    }
    @TestAttribute fun consumerImplementationsFillImportedInterfaceSlots() {
        assertEquals("23", text<Any>(ConsumerVariantSource()))
        val sink = ConsumerVariantSink()
        write<Int>(sink, 29)
        assertEquals("29", sink.last)
    }
    @TestAttribute fun importedProjectedCallbacksAndReferences() {
        val values = arrayOf<Source<Any>>(IntSource(), TextSource())
        assertEquals("value:17", importedVariantCallback(values, "value:"))
        assertEquals("17", importedVariantReference(values))
    }
}
