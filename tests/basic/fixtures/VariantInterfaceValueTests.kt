import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

interface VariantValueSource<out T> { fun read(): T }
class VariantIntSource : VariantValueSource<Int> { override fun read(): Int = 7 }
class VariantTextSource : VariantValueSource<String> { override fun read(): String = "hello" }
interface VariantValueSink<in T> { fun accept(value: T) }
class VariantAnySink : VariantValueSink<Any> {
    var result = ""
    override fun accept(value: Any) { result = value.toString() }
}
class VariantStorage<T>(val value: T)

fun <T> readVariantValue(source: VariantValueSource<T>): T = source.read()
fun <T> variantValueText(source: VariantValueSource<T>): String = source.read().toString()
fun <T> writeVariantValue(sink: VariantValueSink<T>, value: T) = sink.accept(value)
fun widenedVariantValue(): VariantValueSource<Any> = VariantIntSource()
fun <T, R> transformVariantFirst(values: Array<out T>, transform: (T) -> R): R = transform(values[0])
fun <T> variantCallback(values: Array<out VariantValueSource<T>>): String =
    transformVariantFirst(values) { it.read().toString() }
fun <T> variantCapturedCallback(values: Array<out VariantValueSource<T>>, prefix: String): String =
    transformVariantFirst(values) { prefix + it.read().toString() }
fun <T> variantReferencedCallback(values: Array<out VariantValueSource<T>>): String =
    transformVariantFirst(values, ::variantValueText)

class VariantInterfaceValueTests {
    @TestAttribute fun valueTypeCovarianceAtOrdinaryCall() {
        assertEquals("7", variantValueText<Any>(VariantIntSource()))
        assertEquals("hello", variantValueText<Any>(VariantTextSource()))
    }
    @TestAttribute fun genericResultPreservesActualValue() {
        assertEquals(8, readVariantValue<Int>(VariantIntSource()) + 1)
        assertEquals("hello!", readVariantValue<String>(VariantTextSource()) + "!")
    }
    @TestAttribute fun covariantReturnAndNestedStorage() {
        val source = widenedVariantValue()
        val original = VariantIntSource()
        val storage = VariantStorage<VariantValueSource<Any>>(original)
        assertEquals("7", source.read().toString())
        assertEquals("7", storage.value.read().toString())
        check(storage.value === original)
    }
    @TestAttribute fun valueTypeContravarianceAtOrdinaryCall() {
        val sink = VariantAnySink()
        writeVariantValue<Int>(sink, 42)
        assertEquals("42", sink.result)
        writeVariantValue<String>(sink, "text")
        assertEquals("text", sink.result)
    }
    @TestAttribute fun projectedArrayLambdaUsesCarrier() {
        assertEquals("7", variantCallback<Any>(arrayOf(VariantIntSource(), VariantTextSource())))
        assertEquals("hello", variantCallback<Any>(arrayOf(VariantTextSource(), VariantIntSource())))
    }
    @TestAttribute fun projectedArrayCapturedLambdaUsesCarrier() {
        assertEquals("value:7", variantCapturedCallback<Any>(
            arrayOf(VariantIntSource(), VariantTextSource()), "value:"))
    }
    @TestAttribute fun projectedArrayReferenceUsesCarrier() {
        assertEquals("7", variantReferencedCallback<Any>(arrayOf(VariantIntSource(), VariantTextSource())))
    }
}
