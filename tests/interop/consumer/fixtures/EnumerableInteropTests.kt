// C#-producer roundtrip consumer battery B — .NET ENUMERABLE / enum / delegate / nullable-value-type interop.
//   netenum    <- il-netenum    `for (x in <.NET IEnumerable<T>>)` over a raw .NET enumerable (GetEnumerator path)
//   netinterop <- il-netinterop  .NET enum (read/pass/==/when), generic delegates (Func + custom Mapper<T>),
//                                nullable value types (int?/double? <-> Int?/Double? both directions)
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
// il-netenum
import Kfc.Nums
import Kfc.Words
// il-netinterop
import I4.Probe
import I4.Color
import I4.GenDel

// il-netinterop top-level helper: a `when` over the injected .NET enum (unique name to avoid collisions).
fun netInteropDescribe(c: Color): String = when (c) {
    Color.Red -> "warm"
    Color.Green -> "fresh"
    else -> "cool"
}

class EnumerableInteropTests {
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
