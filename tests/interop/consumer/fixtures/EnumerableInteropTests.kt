// C#-producer roundtrip consumer battery B — .NET ENUMERABLE / enum / delegate / nullable-value-type interop.
//   netenum    <- il-netenum    `for (x in <.NET IEnumerable<T>>)` over a raw .NET enumerable (GetEnumerator path)
//   netinterop <- il-netinterop  .NET enum (read/pass/==/when), generic delegates (Func + custom Mapper<T>),
//                                nullable value types (int?/double? <-> Int?/Double? both directions)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
// il-netenum
import Kfc.Nums
import Kfc.Words
import Kfc.EnumerationProbe
import System.Collections.Generic.IEnumerable as ClrEnumerable
// il-netinterop
import I4.Probe
import I4.Color
import I4.GenDel

// il-netinterop top-level helper: a `when` over the projected .NET enum (unique name to avoid collisions).
fun netInteropDescribe(c: Color): String = when (c) {
    Color.Red -> "warm"
    Color.Green -> "fresh"
    else -> "cool"
}

private fun <T> firstNativeElement(values: ClrEnumerable<T>): T? {
    for (value in values) return value
    return null
}

private fun sumNativeElements(values: ClrEnumerable<Int>): Int {
    var sum = 0
    for (value in values) sum += value
    return sum
}

class EnumerableInteropTests {
    @TestAttribute
    fun nativeInterfaceIteration() {
        val ints = EnumerationProbe<Int>(arrayOf(10, 20, 30))
        val view: ClrEnumerable<Int> = ints.Source()
        assertEquals(0, ints.Enumerators)
        assertEquals(0, ints.Moves)
        assertEquals(60, sumNativeElements(view))
        assertEquals(1, ints.Sources)
        assertEquals(1, ints.Enumerators)
        assertEquals(4, ints.Moves)
        assertEquals(3, ints.Currents)
        // Kotlin's iterator protocol does not promise automatic IDisposable.Dispose, including on break.
        assertEquals(0, ints.Disposals)
        val words = EnumerationProbe<String>(arrayOf("a", "bb", "ccc"))
        var joined = ""
        for (word in words.Source()) joined += word
        assertEquals("abbccc", joined)
        val partial = EnumerationProbe<Int>(arrayOf(4, 5, 6))
        for (value in partial.Source()) { assertEquals(4, value); break }
        assertEquals(1, partial.Sources)
        assertEquals(1, partial.Enumerators)
        assertEquals(1, partial.Moves)
        assertEquals(1, partial.Currents)
        assertEquals(0, partial.Disposals)
        val empty = EnumerationProbe<Int>(emptyArray<Int>())
        assertEquals(0, sumNativeElements(empty.Source()))
        assertEquals(1, empty.Moves)
        assertEquals(0, empty.Currents)
        assertEquals(7, firstNativeElement(EnumerationProbe<Int>(arrayOf(7, 8)).Source()))
        assertEquals("first", firstNativeElement(words.Source()))
        assertEquals(null, firstNativeElement(empty.Source()))
        assertEquals(false, ("text" as Any) is Iterable<*>)
    }

    @TestAttribute
    fun nativeIterationInExceptionRegions() {
        val failing = EnumerationProbe<Int>(arrayOf(1))
        failing.FailMove = true
        var caught = false
        try { for (value in failing.Source()) error("must not consume") }
        catch (error: System.InvalidOperationException) { caught = error === failing.Failure }
        assertEquals(true, caught)
        assertEquals(1, failing.Moves)
        assertEquals(0, failing.Currents)
        var result = ""
        try { for (word in EnumerationProbe<String>(arrayOf("t")).Source()) result += word }
        finally { for (word in EnumerationProbe<String>(arrayOf("f")).Source()) result += word }
        try { throw System.InvalidOperationException("catch-loop") }
        catch (error: System.InvalidOperationException) {
            for (word in EnumerationProbe<String>(arrayOf("c")).Source()) result += word
        }
        assertEquals("tfc", result)
        // Resource-owning callers retain explicit control over the native enumerator's lifetime.
        val owned = EnumerationProbe<Int>(arrayOf(9, 10))
        val enumerator = owned.Source().GetEnumerator()
        try { assertEquals(true, (enumerator as System.Collections.IEnumerator).MoveNext()) }
        finally { enumerator.Dispose() }
        assertEquals(1, owned.Disposals)
    }

    @TestAttribute
    fun netenum() {
        var sum = 0
        for (a in Nums()) { sum += a }
        assertEquals(60, sum)                 // 60 — 10+20+30
        var total = 0
        for (w in Words()) { total += w.length }
        assertEquals(6, total)                // 6 — 1+2+3
        val sb = StringBuilder()
        for (w in Words()) { sb.append(w) }
        assertEquals("abbccc", sb.toString()) // abbccc
    }

    @TestAttribute
    fun netinterop() {
        val p = Probe()
        val c = p.First()
        assertEquals("Green", p.NameOf(c))            // Green
        assertEquals(4, p.Code(Color.Blue))           // 4
        assertEquals(true, c == Color.Green)          // True
        assertEquals("fresh", netInteropDescribe(c))  // fresh
        assertEquals("cool", netInteropDescribe(Color.Blue)) // cool
        assertEquals(15, p.Apply({ x -> x + 5 }, 10)) // 15
        assertEquals(18, GenDel().Run({ v -> v * 3 }, 2)) // 18
        assertEquals(42, p.OrZero(p.MaybeVal(true)))  // 42
        assertEquals(0, p.OrZero(p.MaybeVal(false)))  // 0
        assertEquals(7, p.OrZero(7))                  // 7
        assertEquals(0, p.OrZero(null))               // 0
        assertEquals(1.5, p.Half(3.0))                // 1.5
    }
}
