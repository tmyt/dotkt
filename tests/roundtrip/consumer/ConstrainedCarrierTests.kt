import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import constrainedcarrier.ReferencedSink
import constrainedcarrier.createReferencedSink
import constrainedcarrier.ReferencedAnimal
import constrainedcarrier.ReferencedDog

class ConsumerConstrainedSink : ReferencedSink<ReferencedAnimal>() {
    override fun <R : ReferencedAnimal> render(value: R): String = "consumer"
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
    }
}
