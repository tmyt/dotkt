import NUnit.Framework.TestAttribute
import ArrayListViews.NativeArrayWrites

class NativeArrayListViewTests {
    @TestAttribute
    fun nativeArrayMutationsRemainVisibleThroughKotlinListViews() {
        val ints = NativeArrayWrites.Integers()
        val view = ints.asList()
        val iterator = view.iterator()
        val sub = view.subList(1, 3)
        NativeArrayWrites.WriteInts(ints, 7)
        check(view[0] == 7 && sub[1] == 7 && iterator.next() == 7)
        val strings = NativeArrayWrites.Strings()
        val stringsView = strings.asList()
        NativeArrayWrites.Write(strings, "last")
        check(stringsView[0] == "last" && stringsView[2] == "last")
        val durations = NativeArrayWrites.Durations()
        val durationsView = durations.asList()
        NativeArrayWrites.Write(durations, System.TimeSpan(0, 0, 7))
        check(durationsView[0].TotalSeconds == 7.0 && durationsView[2].TotalSeconds == 7.0)
        val primitive = intArrayOf(1, 2, 1)
        val primitiveView = primitive.asList()
        NativeArrayWrites.WriteInts(primitive, 9)
        check(primitiveView[0] == 9 && primitiveView[2] == 9)
    }
}
