import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.AreEqual as assertEquals

enum class EnumRendered(val amount: Int) {
    SMALL(1), LARGE(2);
    override fun toString(): String = "rendered:$amount"
}

enum class EnumEntryRendered(val amount: Int) {
    SPECIAL(1) { override fun toString(): String = "entry" },
    PLAIN(2);
    override fun toString(): String = "base:$amount"
}

enum class EnumAbstractRendered {
    FIRST { override fun toString(): String = "first" },
    SECOND { override fun toString(): String = "second" };
    abstract override fun toString(): String
}

enum class EnumDefaultRendered(val amount: Int) {
    FIRST(1), SECOND(2);
    fun toString(prefix: String): String = "$prefix:$amount"
    companion object {
        override fun toString(): String = "companion"
    }
}

enum class EnumEntryDefaultRendered {
    SPECIAL { override fun toString(): String = "special" },
    PLAIN
}

class EnumRenderingTests {
    @TestAttribute
    fun authoredOverridesOwnTheEnumObjectSlot() {
        assertEquals("rendered:2", EnumRendered.LARGE.toString())
        val opaque: Any = EnumRendered.SMALL
        assertEquals("rendered:1", opaque.toString())
        assertEquals("entry", EnumEntryRendered.SPECIAL.toString())
        assertEquals("base:2", EnumEntryRendered.PLAIN.toString())
        val entry: Any = EnumEntryRendered.SPECIAL
        assertEquals("entry", entry.toString())
        assertEquals("first", EnumAbstractRendered.FIRST.toString())
        val abstractEntry: Any = EnumAbstractRendered.SECOND
        assertEquals("second", abstractEntry.toString())
    }

    @TestAttribute
    fun inheritedNamesSurviveOverloadsCompanionsAndEntryOverrides() {
        assertEquals("FIRST", EnumDefaultRendered.FIRST.toString())
        assertEquals("SECOND", EnumDefaultRendered.SECOND.toString())
        assertEquals("prefix:2", EnumDefaultRendered.SECOND.toString("prefix"))
        assertEquals("companion", EnumDefaultRendered.Companion.toString())
        assertEquals("special", EnumEntryDefaultRendered.SPECIAL.toString())
        assertEquals("PLAIN", EnumEntryDefaultRendered.PLAIN.toString())
        val plain: Any = EnumEntryDefaultRendered.PLAIN
        assertEquals("PLAIN", plain.toString())
    }
}
