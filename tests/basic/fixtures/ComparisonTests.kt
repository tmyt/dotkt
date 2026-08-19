// Comparison battery (feature fixture) — user Comparable/Comparator impls and ordinal String.compareTo. Migrates
// the comparison family of cases/il-* onto the in-process NUnit suite. Each old case's `main` + stdout-golden diff
// becomes one @TestAttribute method whose per-value assert is strictly stronger (typed Int/Boolean) than the old text
// diff. Every value the old il_check asserted is preserved 1:1 (see the `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   il-comparable -> userComparable_selfGeneric   class : Comparable<Ver> -> CLR IComparable<V>; </>/<=/sorted()
//   il-comparator -> userComparator               class : Comparator<Int> -> CLR IComparer<T> (compare -> Compare)
//   il-cmpord     -> stringCompareTo_ordinal       String.compareTo is ORDINAL (UTF-16 code unit), not culture-sensitive
//
// Assembly-wide collision rule: every top-level declaration is `Comparison`-prefixed (Ver -> ComparisonVer, IntCmp -> ComparisonIntCmp).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.IsFalse as assertFalse

// ---- il-comparable : user class implementing Kotlin's Comparable<V> (self-referential generic) -----------------
class ComparisonVer(val major: Int, val minor: Int) : Comparable<ComparisonVer> {
    override fun compareTo(o: ComparisonVer): Int = if (major != o.major) major - o.major else minor - o.minor
    override fun toString(): String = "" + major + "." + minor
}

// Kotlin permits Nothing as a covariant override result for Comparable.compareTo. The physical generic and
// non-generic IComparable slots still return Int32; both forwarding paths must terminate instead of returning the
// CLR object erasure of Nothing into that slot (#321).
private class ComparisonNothing : Comparable<ComparisonNothing> {
    override fun compareTo(other: ComparisonNothing): Nothing =
        throw IllegalStateException("comparison does not return")
}

private fun <T : Comparable<T>> comparisonGenericCompare(left: T, right: T): Int = left.compareTo(right)

private class ComparisonNothingOverload : Comparable<ComparisonNothingOverload> {
    fun CompareTo(other: String): Nothing = throw IllegalStateException(other)
    override fun compareTo(other: ComparisonNothingOverload): Int = 0
}

private class ComparisonAnyNullable : Comparable<Any?> {
    override fun compareTo(other: Any?): Int = if (other == null) 1 else 0
}

// ---- il-comparator : user class implementing Kotlin's Comparator<T> -> CLR IComparer<T> -----------------------
class ComparisonIntCmp : Comparator<Int> {
    override fun compare(a: Int, b: Int): Int = a - b
}

class ComparisonTests {
    @TestAttribute
    fun selfGeneric() {
        val a = ComparisonVer(1, 2); val b = ComparisonVer(1, 5); val c = ComparisonVer(2, 0)
        assertTrue(a < b)                                       // a<b   (</> desugar to compareTo)
        assertTrue(c > b)                                       // c>b
        assertTrue(a <= a)                                      // a<=a
        assertEquals(-3, a.compareTo(b))                        // -3
        val sorted = listOf(c, a, b).sorted()                   // sorted() uses compareTo
        assertEquals("1.2,1.5,2.0", sorted.joinToString(","))   // 1.2,1.5,2.0

        // The derived declaration is compiled from an earlier-sorted file than its base. Loading and dispatching it
        // proves the late non-generic IComparable interface manifest was built from the completed module graph.
        val crossFileLow = CrossFileComparableDerived(2)
        val crossFileHigh = CrossFileComparableDerived(7)
        assertEquals(-5, crossFileLow.compareTo(crossFileHigh))
        assertTrue(compareValues(crossFileLow, crossFileHigh) < 0)
    }

    @TestAttribute
    fun nothingReturningComparableLoadsAndTerminatesBothSlots() {
        val value = ComparisonNothing()
        assertEquals("comparison does not return",
            try { value.compareTo(value) } catch (e: IllegalStateException) { e.message })
        assertEquals("comparison does not return",
            try { comparisonGenericCompare(value, value) } catch (e: IllegalStateException) { e.message })
        assertEquals("comparison does not return",
            try { compareValues(value, value) } catch (e: IllegalStateException) { e.message })

        val overloaded = ComparisonNothingOverload()
        assertEquals(0, compareValues(overloaded, overloaded))
        val anyNullable = ComparisonAnyNullable()
        assertEquals(0, compareValues(anyNullable, anyNullable))
    }

    @TestAttribute
    fun userComparator() {
        val c = ComparisonIntCmp()
        assertEquals(-3, c.compare(2, 5))   // -3
        assertEquals(5, c.compare(9, 4))    // 5
        assertEquals(0, c.compare(3, 3))    // 0
    }

    @TestAttribute
    fun ordinal() {
        assertEquals(31, "a".compareTo("B"))      // 31  ('a'=97 - 'B'=66)
        assertEquals(-31, "B".compareTo("a"))     // -31
        assertEquals(0, "abc".compareTo("abc"))   // 0
        assertEquals(-1, "abc".compareTo("abd"))  // -1
        assertFalse("a" < "B")                    // false (ordinal: 'a' > 'B')
        assertEquals(-7, "Z".compareTo("a"))      // -7  ('Z'=90 - 'a'=97), uppercase sorts before lowercase
    }
}
