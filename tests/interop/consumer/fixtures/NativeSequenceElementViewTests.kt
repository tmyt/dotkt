import NUnit.Framework.TestAttribute
import SequenceViews.NativeSequences

class NativeSequenceElementViewTests {
    @TestAttribute
    fun exactNativeEnumerableSignaturesAndKotlinViewsRemainDistinct() {
        val native = NativeSequences.Integers()
        check(NativeSequences.Sum(native) == 8)
        val source = native as Sequence<Int>
        val widened: Sequence<Any> = source
        check(widened === source && source === native)
        check(widened.filterIsInstance<Int>().toList() == listOf(3, 5))
        check(source.iterator().next() == 3)
        val mixed = NativeSequences.Mixed() as Sequence<Any>
        check(mixed.filterIsInstance<String>().toList() == listOf("a", "b"))
        check(mixed.filterIsInstance<Int>().single() == 7)
    }
}
