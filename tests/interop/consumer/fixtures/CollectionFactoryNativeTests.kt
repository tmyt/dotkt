import NUnit.Framework.TestAttribute
import ArrayListViews.NativeArrayWrites
import System.Collections.Generic.IReadOnlyList
import System.Collections.Generic.IList
import System.Collections.Generic.ISet

private fun <T> forwardedNativeArrayList(values: Array<T>): ArrayList<T> = arrayListOf(*values)

class CollectionFactoryNativeTests {
    @TestAttribute fun factoryListsRetainTheirNativeClosedElementFaces() {
        val emptyListView = emptyList<Int>() as IReadOnlyList<Int>
        check(emptyListView.Count == 0)
        val emptySetView = emptySet<Int>() as System.Collections.Generic.IReadOnlyCollection<Int>
        check(emptySetView.Count == 0)
        val list = listOf(1, 2, 3)
        check(NativeArrayWrites.First(list as IReadOnlyList<Int>) == 1)
        check(NativeArrayWrites.Sum(list as IReadOnlyList<Int>) == 6)
        val mutable = mutableListOf(1, 2, 3)
        check(NativeArrayWrites.Sum(mutable as IReadOnlyList<Int>) == 6)
        (mutable as IList<Int>).Add(4)
        check(mutable.size == 4 && mutable[3] == 4)
        val arrayList = arrayListOf("first", "second")
        check(NativeArrayWrites.First(arrayList as IReadOnlyList<String>) == "first")
        (arrayList as IList<String>).Add("third")
        check(arrayList.size == 3 && arrayList[2] == "third")
        val singleton = listOf(System.TimeSpan.FromTicks(7))
        check(NativeArrayWrites.First(singleton as IReadOnlyList<System.TimeSpan>).Ticks == 7L)
        val copied = forwardedNativeArrayList(arrayOf(3, 4))
        check(NativeArrayWrites.First(copied as IReadOnlyList<Int>) == 3)
        (copied as IList<Int>).Add(5)
        check(copied.size == 3 && copied[2] == 5)
        val copiedNative = forwardedNativeArrayList(arrayOf(System.TimeSpan.FromTicks(11)))
        check(NativeArrayWrites.First(copiedNative as IReadOnlyList<System.TimeSpan>).Ticks == 11L)
        val primitive = intArrayOf(1, 2, 3)
        check(C1Net.Util.Sum(*primitive) == 6)
        check(System.String.Concat(*arrayOf<Any?>("a", 2, null)) == "a2")
        val foreign = System.Collections.Generic.List<Int>()
        foreign.Add(5)
        foreign.Add(6)
        check((foreign as Collection<*>).indices.last == 1)
    }

    @TestAttribute fun factorySetsRetainNativeMutationAndKotlinComparer() {
        val values = hashSetOf(1, 2, 1)
        val native = values as ISet<Int>
        check(native.Contains(2))
        check(native.Add(3) && !native.Add(3))
        check(values.size == 3 && values.contains(3))
        val floating = hashSetOf(0.0, -0.0, Double.NaN)
        val nativeFloating = floating as ISet<Double>
        check(nativeFloating.Contains(0.0) && nativeFloating.Contains(-0.0))
        check(nativeFloating.Contains(Double.NaN))
        check(nativeFloating.Remove(-0.0))
        check(floating.contains(0.0) && !floating.contains(-0.0))
    }
}
