// Enum rich-API battery (batch MigM, from cases/m-a8) — the ONLY pure-Kotlin (JVM-oracle-backed) proof of the
// full `enum class` reflective surface: name / ordinal / valueOf / values() / entries. Migrated onto the
// in-process NUnit suite; each old case's `main` + JVM golden becomes one @TestAttribute method whose per-value
// assert is strictly stronger (typed) than the old stdout diff; every asserted value preserved 1:1 (see
// `// <expected>`). The ordered values() loop (was ordered `println`s) is captured into a log list and asserted
// in order.
//
// Coverage preserved (old case -> method):
//   m-a8  -> enumRichApi   name / ordinal / valueOf("BLUE") / values() (ordered) / entries.size
//
// Top-level names are MigM-prefixed (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

enum class MigMColor { RED, GREEN, BLUE }

class EnumApiTests {
    @TestAttribute
    fun enumRichApi() {
        val c = MigMColor.GREEN
        assertEquals("GREEN", c.name)               // GREEN
        assertEquals(1, c.ordinal)                  // 1
        assertEquals(MigMColor.BLUE, MigMColor.valueOf("BLUE"))  // BLUE (valueOf)

        val names = mutableListOf<String>()
        for (x in MigMColor.values()) names.add(x.name)  // ordered values() loop
        assertEquals(3, names.size)
        assertEquals("RED", names[0])               // RED
        assertEquals("GREEN", names[1])             // GREEN
        assertEquals("BLUE", names[2])              // BLUE

        assertEquals(3, MigMColor.entries.size)     // 3 (entries)
    }
}
