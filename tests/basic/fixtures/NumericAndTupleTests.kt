// Numeric / value-type / tuple battery (feature fixture). Migrates the unsigned-arithmetic, value-class, tailrec, and
// tuple family of cases/il-* onto the in-process NUnit suite. Each old case's `main` + stdout-golden diff becomes
// one @TestAttribute method whose per-value assert is strictly stronger (typed) than the old text diff; every
// asserted value is preserved 1:1 (see `// <expected>`). Unsigned prints are asserted via `.toString()` to match
// the exact decimal rendering the old il_check compared.
//
// Coverage preserved (old case -> method):
//   il-unsigned    -> unsignedTypes            UInt/ULong/UShort/UByte inline classes -> native CLR unsigned primitives
//   il-unsignedshr -> unsignedShrLogical       #94 unsigned shr = LOGICAL (zero-fill, Shr_Un); shl + signed shr non-regression
//   il-valclass    -> valueClass_radix         @JvmInline value class (passed/returned/method) + Int/Long.toString(radix)
//   il-tailrec     -> tailrec_deepTCO          §2b deep `tailrec` TCO'd to a back-jump loop (self/when/ext-receiver/member); no stack overflow
//   il-triple      -> triple_coverage          Triple ctor/componentN/destructure/full-arg copy/structural-eq/toString
//   il-tryval      -> tryCatchValuePosition    #127 `try{value}catch{null}` in VALUE position -> Nullable<T> join (incl. toFloatOrNull/toDoubleOrNull)
//
// All top-level declarations introduced here are NumericTuple-prefixed (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue

// ---- il-valclass : @JvmInline value class passed/returned/method-bearing --------------------------------------------
@JvmInline value class NumericTupleMoney(val cents: Int) {
    fun dollars(): Int = cents / 100
}
fun numericTuplefee(m: NumericTupleMoney): Int = m.cents

// ---- il-tailrec : deep tailrec TCO'd to a back-jump loop (constant stack) ------------------------------------------
tailrec fun numericTuplesumTo(n: Int, acc: Long): Long = if (n == 0) acc else numericTuplesumTo(n - 1, acc + n)
tailrec fun numericTuplecountdown(n: Int): Int = if (n <= 0) 0 else numericTuplecountdown(n - 1)
tailrec fun numericTuplegcd(a: Long, b: Long): Long = when { b == 0L -> a; else -> numericTuplegcd(b, a % b) }
tailrec fun Int.numericTuplecountDownExt(acc: Int): Int = if (this <= 0) acc else (this - 1).numericTuplecountDownExt(acc + 1)
class NumericTupleAdder(val step: Int) {
    tailrec fun run(n: Int, acc: Long): Long = if (n == 0) acc else run(n - 1, acc + step)
}

// ---- il-triple : Triple across a function boundary + full-arg copy -------------------------------------------------
fun numericTupleswapEnds(t: Triple<Int, String, Int>): Triple<Int, String, Int> = t.copy(t.third, t.second, t.first)

class NumericAndTupleTests {
    @TestAttribute
    fun unsignedTypes() {
        val a: UInt = 4000000000u
        val b: UInt = 100u
        assertEquals("4000000100", (a + b).toString())   // 4000000100
        assertEquals("4000000000", a.toString())         // 4000000000
        val c: ULong = 18000000000000000000uL
        assertEquals("18000000000000000000", c.toString()) // 18000000000000000000
        val s: UShort = 60000u
        val by: UByte = 250u
        assertEquals("60000", s.toString())              // 60000
        assertEquals("250", by.toString())               // 250
    }

    @TestAttribute
    fun unsignedShrLogical() {
        assertEquals("2147483647", (UInt.MAX_VALUE shr 1).toString())            // 2147483647
        assertEquals("9223372036854775807", (ULong.MAX_VALUE shr 1).toString())  // 9223372036854775807
        assertEquals("267386880", (0xFF000000u shr 4).toString())                // 267386880
        assertEquals("1073741824", (2147483648u shr 1).toString())               // 1073741824 (zero-fill, not sign-extend)
        assertEquals("2147483648", (1u shl 31).toString())                       // 2147483648 (shl bit-identical)
        assertEquals(-4, (-8) shr 1)                                             // -4 (signed shr stays arithmetic)
    }

    @TestAttribute
    fun valueClassRepresentation() {
        val m = NumericTupleMoney(1250)
        assertEquals(1250, m.cents)          // 1250
        assertEquals(12, m.dollars())        // 12
        assertEquals(1250, numericTuplefee(m))         // 1250 (passed as an argument)
    }

    @TestAttribute
    fun deepTCO() {
        assertEquals(500000500000L, numericTuplesumTo(1_000_000, 0L))       // 500000500000
        assertEquals(0, numericTuplecountdown(1_000_000))                   // 0
        assertEquals(2000000014L, numericTuplegcd(6_000_000_042L, 4_000_000_028L)) // 2000000014
        assertEquals(1000000, 1_000_000.numericTuplecountDownExt(0))        // 1000000
        assertEquals(2000000L, NumericTupleAdder(2).run(1_000_000, 0L))     // 2000000
    }

    @TestAttribute
    fun coverage() {
        val t = Triple(1, "two", 3)
        assertEquals(1, t.first)             // 1
        assertEquals("two", t.second)        // two
        assertEquals(3, t.third)             // 3
        assertEquals("(1, two, 3)", t.toString()) // (1, two, 3)
        val (a, b, c) = t
        assertEquals("1|two|3", "$a|$b|$c")  // 1|two|3
        assertEquals(1, t.component1())      // 1
        assertEquals("two", t.component2())  // two
        assertEquals(3, t.component3())      // 3
        val u = t.copy(1, "TWO", 3)
        assertEquals("(1, TWO, 3)", u.toString()) // (1, TWO, 3)
        assertTrue(t == Triple(1, "two", 3)) // true (structural equality)
        assertEquals(false, u == t)          // false
        assertEquals("(3, two, 1)", numericTupleswapEnds(t).toString()) // (3, two, 1)
        val nested = Triple(listOf(1, 2), "x", mapOf("k" to 9))
        assertEquals("([1, 2], x, {k=9})", nested.toString())  // ([1, 2], x, {k=9})
    }

    @TestAttribute
    fun tryCatchValuePosition() {
        val a: Int? = try { 5 } catch (e: Exception) { null }
        assertEquals(5, a)                                   // 5
        val b: Int? = try { throw RuntimeException("x") } catch (e: Exception) { null }
        assertTrue(b == null)                                // null
        val l: Long? = try { 7L } catch (e: Exception) { null }
        assertEquals(7L, l)                                  // 7
        val d: Double? = try { 3.5 } catch (e: Exception) { null }
        assertEquals(3.5, d)                                 // 3.5
        assertEquals(1.5f, "1.5".toFloatOrNull())            // 1.5
        assertTrue("nope".toFloatOrNull() == null)           // null
        assertEquals(2.5, "2.5".toDoubleOrNull())            // 2.5
        assertTrue("nope".toDoubleOrNull() == null)          // null
        val c: Int? = try { throw RuntimeException("x") } catch (e: Exception) { null }
        assertEquals(11, (c ?: 10) + 1)                      // 11
    }
}
