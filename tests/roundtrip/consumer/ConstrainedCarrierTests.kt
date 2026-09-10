import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import constrainedcarrier.ReferencedSink
import constrainedcarrier.createReferencedSink
import constrainedcarrier.ReferencedAnimal
import constrainedcarrier.ReferencedDog
import constrainedcarrier.compareReferencedGeneric
import constrainedcarrier.deferReferencedGeneric

class ConsumerConstrainedSink : ReferencedSink<ReferencedAnimal>() {
    override fun <R : ReferencedAnimal> render(value: R): String {
        val outer = {
            val inner = { value.toString(); "consumer" }
            inner()
        }
        return outer()
    }
}

class ConsumerGenericComparer : System.Collections.Generic.IComparer<Any> {
    override fun Compare(x: Any?, y: Any?): Int = x.toString().toInt()
}
class ConsumerGenericValue<out T>(val item: T) {
    fun <R : System.Collections.Generic.IComparer<T>> compare(value: R): Int = compareReferencedGeneric<T, R>(value, item)
    fun <R : System.Collections.Generic.IComparer<T>> deferred(value: R): () -> Int = deferReferencedGeneric<T, R>(value, item)
}

class ConstrainedCarrierRoundtripTests {
    @TestAttribute
    fun constrainedMethodsRetainSourceBoundsAndDispatchAcrossAssemblies() {
        val strings: ReferencedSink<String> = createReferencedSink()
        assertEquals("text", strings.echo("text"))
        assertEquals("derived:virtual", strings.render("virtual"))
        val integers: ReferencedSink<Int> = createReferencedSink()
        assertEquals(42, integers.echo(42))
        assertEquals("43", integers.callback(42) { (it + 1).toString() })
        val animals: ReferencedSink<ReferencedDog> = ConsumerConstrainedSink()
        assertEquals("consumer", animals.render(ReferencedDog()))
        val generic: ConsumerGenericValue<Any> = ConsumerGenericValue(29)
        assertEquals(29, generic.compare(ConsumerGenericComparer()))
        assertEquals(29, generic.deferred(ConsumerGenericComparer())())
    }
}
