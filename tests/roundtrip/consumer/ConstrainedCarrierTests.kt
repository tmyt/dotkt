import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import constrainedcarrier.ReferencedSink
import constrainedcarrier.createReferencedSink
import constrainedcarrier.ReferencedAnimal
import constrainedcarrier.ReferencedDog
import constrainedcarrier.compareReferencedGeneric
import constrainedcarrier.deferReferencedGeneric
import constrainedcarrier.headReferencedNative
import constrainedcarrier.countReferencedNative

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

class ConsumerNativeListView<in T> {
    fun <R : System.Collections.Generic.IReadOnlyList<T>> head(value: R): Any? = headReferencedNative<T, R>(value)
    fun <R : System.Collections.Generic.IReadOnlyList<T>> count(value: R): Int = countReferencedNative<T, R>(value)
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

    @TestAttribute
    fun projectedNativeConstraintKeepsInheritedPropertyAndIndexer() {
        val narrowed: ConsumerNativeListView<Int> = ConsumerNativeListView<Any>()
        val list = System.Collections.Generic.List<Int>()
        list.Add(37)
        assertEquals(37, narrowed.head(list))
        assertEquals(1, narrowed.count(list))
    }
}
