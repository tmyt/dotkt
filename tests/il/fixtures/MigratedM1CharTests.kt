// Char / String-case battery (migration batch M1) — Char predicates & code-point casts, String->number parse, and
// CLR-native 1:1 case mapping. Migrates the char/text family of cases/il-* onto the in-process NUnit suite. Each old
// case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value assert is strictly stronger
// (typed Char/Int/Boolean/String) than the old text diff. Every value the old il_check asserted is preserved 1:1.
//
// Coverage preserved (old case -> method):
//   il-char         -> charOps                Char predicates -> System.Char statics; code<->Char casts
//   il-cp           -> stringParse_charPreds  String->number (Int32.Parse/Double.Parse) + Char predicates
//   il-caseinvariant-> caseMapping_oneToOne   #144 uppercase()/lowercase() = CLR-native 1:1 (ß stays ß, NOT SS);
//                                             DELIBERATELY no Unicode one-to-many expansion (docs/dotkt-semantics §5g)
//
// Batch-M1 collision rule: no top-level declarations introduced (all bodies self-contained).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.Companion.IsTrue as assertTrue

class MigratedM1CharTests {
    @TestAttribute
    fun charOps() {
        val c = 'a'
        assertTrue(c.isLetter())            // true
        assertTrue('7'.isDigit())           // true
        assertTrue(' '.isWhitespace())      // true
        assertTrue(c.isLetterOrDigit())     // true
        assertEquals('A', c.uppercaseChar()) // A
        assertEquals('z', 'Z'.lowercaseChar()) // z
        assertTrue('Q'.isUpperCase())       // true
        assertTrue(c.isLowerCase())         // true
        assertEquals(97, c.code)            // 97  (Char -> Int code point)
        assertEquals('b', 98.toChar())      // b   (Int -> Char)
    }

    @TestAttribute
    fun stringParse_charPreds() {
        assertEquals(50, "42".toInt() + 8)     // 50
        assertEquals(3.5, "3.5".toDouble())    // 3.5
        assertTrue('7'.isDigit())              // true
        assertTrue('a'.isLetter())             // true
        assertEquals('X', 'x'.uppercaseChar()) // X
    }

    @TestAttribute
    fun caseMapping_oneToOne() {
        assertEquals("ß", "ß".uppercase())          // ß      (NOT "SS": no one-to-many expansion)
        assertEquals("STRAßE", "straße".uppercase()) // STRAßE  (the ß stays ß)
        assertEquals("ABC", "abc".uppercase())       // ABC    (normal 1:1 mapping works)
        assertEquals("hello", "HELLO".lowercase())   // hello
        assertEquals("ß", 'ß'.uppercase())           // ß      (Char.uppercase(): String, no expansion)
        assertTrue("ß".uppercase() == "ß")           // True
    }
}
