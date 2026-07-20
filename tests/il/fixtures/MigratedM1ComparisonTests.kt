// Comparison battery (migration batch M1) — user Comparable/Comparator impls and ordinal String.compareTo. Migrates
// the comparison family of cases/il-* onto the in-process NUnit suite. Each old case's `main` + stdout-golden diff
// becomes one @TestAttribute method whose per-value assert is strictly stronger (typed Int/Boolean) than the old text
// diff. Every value the old il_check asserted is preserved 1:1 (see the `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   il-comparable -> userComparable_selfGeneric   class : Comparable<Ver> -> CLR IComparable<V>; </>/<=/sorted()
//   il-comparator -> userComparator               class : Comparator<Int> -> CLR IComparer<T> (compare -> Compare)
//   il-cmpord     -> stringCompareTo_ordinal       String.compareTo is ORDINAL (UTF-16 code unit), not culture-sensitive
//
// Batch-M1 collision rule: every top-level declaration is `M1`-prefixed (Ver -> M1Ver, IntCmp -> M1IntCmp).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsFalse as assertFalse

// ---- il-comparable : user class implementing Kotlin's Comparable<V> (self-referential generic) -----------------
class M1Ver(val major: Int, val minor: Int) : Comparable<M1Ver> {
    override fun compareTo(o: M1Ver): Int = if (major != o.major) major - o.major else minor - o.minor
    override fun toString(): String = "" + major + "." + minor
}

// ---- il-comparator : user class implementing Kotlin's Comparator<T> -> CLR IComparer<T> -----------------------
class M1IntCmp : Comparator<Int> {
    override fun compare(a: Int, b: Int): Int = a - b
}

class MigratedM1ComparisonTests {
    @TestAttribute
    fun userComparable_selfGeneric() {
        val a = M1Ver(1, 2); val b = M1Ver(1, 5); val c = M1Ver(2, 0)
        assertTrue(a < b)                                       // a<b   (</> desugar to compareTo)
        assertTrue(c > b)                                       // c>b
        assertTrue(a <= a)                                      // a<=a
        assertEquals(-3, a.compareTo(b))                        // -3
        val sorted = listOf(c, a, b).sorted()                   // sorted() uses compareTo
        assertEquals("1.2,1.5,2.0", sorted.joinToString(","))   // 1.2,1.5,2.0
    }

    @TestAttribute
    fun userComparator() {
        val c = M1IntCmp()
        assertEquals(-3, c.compare(2, 5))   // -3
        assertEquals(5, c.compare(9, 4))    // 5
        assertEquals(0, c.compare(3, 3))    // 0
    }

    @TestAttribute
    fun stringCompareTo_ordinal() {
        assertEquals(31, "a".compareTo("B"))      // 31  ('a'=97 - 'B'=66)
        assertEquals(-31, "B".compareTo("a"))     // -31
        assertEquals(0, "abc".compareTo("abc"))   // 0
        assertEquals(-1, "abc".compareTo("abd"))  // -1
        assertFalse("a" < "B")                    // false (ordinal: 'a' > 'B')
        assertEquals(-7, "Z".compareTo("a"))      // -7  ('Z'=90 - 'a'=97), uppercase sorts before lowercase
    }
}
