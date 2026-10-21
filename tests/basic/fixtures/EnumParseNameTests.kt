import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals
import NUnit.Framework.Legacy.ClassicAssert.IsTrue as assertTrue
import System.DayOfWeek
import System.Enum as ClrEnumApi
import System.Type
import kotlin.clr.ClrEnum

private enum class ParseNameMode { FIRST, SECOND }
private enum class ParseNameEmpty
private enum class ParseNameRich(val number: Int) { FIRST(5), SECOND(9) }
@ClrEnum
private enum class ParseNameExplicit(value: Int) { FIRST(10), SECOND(-4) }
private inline fun <reified E : Enum<E>> parseExactName(name: String): E = enumValueOf<E>(name)
private fun parseNameFails(action: () -> Unit): Boolean {
    try { action() } catch (e: IllegalArgumentException) { return true }
    return false
}

class EnumParseNameTests {
    @TestAttribute
    fun ordinaryNamesAreExactAndArgumentsRunOnce() {
        var calls = 0
        fun name(value: String): String { calls++; return value }
        assertEquals(ParseNameMode.SECOND, ParseNameMode.valueOf(name("SECOND")))
        assertEquals(1, calls)
        for (invalid in listOf("1", "0", "-1", "+1", " FIRST", "FIRST ", "first", "FIRST, SECOND", "")) {
            assertTrue(parseNameFails { ParseNameMode.valueOf(name(invalid)) })
        }
        assertEquals(10, calls)
    }

    @TestAttribute
    fun emptyAndRichEnumsKeepExactNameSemantics() {
        assertTrue(parseNameFails { ParseNameEmpty.valueOf("0") })
        assertTrue(parseNameFails { ParseNameEmpty.valueOf("MISSING") })
        assertEquals(ParseNameRich.SECOND, ParseNameRich.valueOf("SECOND"))
        assertTrue(parseNameFails { ParseNameRich.valueOf("1") })
    }

    @TestAttribute
    fun reifiedParsingUsesTheSelectedEnumDeclaration() {
        assertEquals(ParseNameMode.FIRST, parseExactName<ParseNameMode>("FIRST"))
        assertEquals(ParseNameExplicit.SECOND, parseExactName<ParseNameExplicit>("SECOND"))
        assertEquals(ParseNameRich.SECOND, parseExactName<ParseNameRich>("SECOND"))
        assertEquals(DayOfWeek.Monday, parseExactName<DayOfWeek>("Monday"))
        assertTrue(parseNameFails { parseExactName<ParseNameMode>("1") })
        assertTrue(parseNameFails { parseExactName<ParseNameExplicit>("-4") })
        assertTrue(parseNameFails { parseExactName<ParseNameRich>("1") })
        assertTrue(parseNameFails { parseExactName<ParseNameEmpty>("0") })
        assertTrue(parseNameFails { parseExactName<DayOfWeek>("1") })
        assertTrue(parseNameFails { parseExactName<DayOfWeek>(" Monday ") })
    }

    @TestAttribute
    fun nativeKotlinValueOfIsStrictButExplicitClrParseIsNot() {
        assertEquals(DayOfWeek.Monday, DayOfWeek.valueOf("Monday"))
        assertTrue(parseNameFails { DayOfWeek.valueOf("1") })
        assertTrue(parseNameFails { DayOfWeek.valueOf(" Monday ") })
        assertEquals(DayOfWeek.Monday,
            ClrEnumApi.Parse(Type.GetType("System.DayOfWeek")!!, "1") as DayOfWeek)
    }
}
