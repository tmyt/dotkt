import NUnit.Framework.TestAttribute
import ArrayListViews.NativeArrayWrites
import System.Collections.Generic.IReadOnlyList

class NativeArrayListViewTests {
    @TestAttribute
    fun nativeArrayMutationsRemainVisibleThroughKotlinListViews() {
        val ints = NativeArrayWrites.Integers()
        val view = ints.asList()
        val iterator = view.iterator()
        val sub = view.subList(1, 3)
        NativeArrayWrites.WriteInts(ints, 7)
        check(view[0] == 7 && sub[1] == 7 && iterator.next() == 7)
        val nativeView = view as IReadOnlyList<Int>
        check(NativeArrayWrites.First(nativeView) == 7 && NativeArrayWrites.Sum(nativeView) == 16)
        check(view == listOf(7, 2, 7) && listOf(7, 2, 7) == view)
        check(view.hashCode() == listOf(7, 2, 7).hashCode() && view.toString() == "[7, 2, 7]")
        val strings = NativeArrayWrites.Strings()
        val stringsView = strings.asList()
        NativeArrayWrites.Write(strings, "last")
        check(stringsView[0] == "last" && stringsView[2] == "last")
        check(NativeArrayWrites.First(stringsView as IReadOnlyList<String>) == "last")
        val durations = NativeArrayWrites.Durations()
        val durationsView = durations.asList()
        NativeArrayWrites.Write(durations, System.TimeSpan(0, 0, 7))
        check(durationsView[0].TotalSeconds == 7.0 && durationsView[2].TotalSeconds == 7.0)
        check(NativeArrayWrites.First(durationsView as IReadOnlyList<System.TimeSpan>).TotalSeconds == 7.0)
        val primitive = intArrayOf(1, 2, 1)
        val primitiveView = primitive.asList()
        NativeArrayWrites.WriteInts(primitive, 9)
        check(primitiveView[0] == 9 && primitiveView[2] == 9)
        val nativePrimitiveView = primitiveView as IReadOnlyList<Int>
        check(NativeArrayWrites.First(nativePrimitiveView) == 9 && NativeArrayWrites.Sum(nativePrimitiveView) == 20)
    }
}
