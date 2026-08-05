// Char / String-case battery — Char predicates & code-point casts, and
// CLR-native 1:1 case mapping. Migrates the char/text family of cases/il-* onto the in-process NUnit suite. The
// surviving methods group each compiler shape once and use typed assertions instead of stdout-golden diffs.
//
// Coverage preserved (old case -> method):
//   il-char / il-cp -> charOps                Char predicates -> System.Char statics; code<->Char casts
//                                             (il-cp's numeric parsing lives in StringsTests.numberParsing)
//   il-caseinvariant-> caseMapping_oneToOne   #144 uppercase()/lowercase() = CLR-native 1:1 (ß stays ß, NOT SS);
//                                             DELIBERATELY no Unicode one-to-many expansion (docs/dotkt-semantics §5g)
//
// No top-level declarations are introduced; all bodies are self-contained.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue

class CharacterAndCaseMappingTests {
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
    fun oneToOne() {
        assertEquals("ß", "ß".uppercase())          // ß      (NOT "SS": no one-to-many expansion)
        assertEquals("STRAßE", "straße".uppercase()) // STRAßE  (the ß stays ß)
        assertEquals("ABC", "abc".uppercase())       // ABC    (normal 1:1 mapping works)
        assertEquals("hello", "HELLO".lowercase())   // hello
        assertEquals("ß", 'ß'.uppercase())           // ß      (Char.uppercase(): String, no expansion)
        assertTrue("ß".uppercase() == "ß")           // True
    }
}
