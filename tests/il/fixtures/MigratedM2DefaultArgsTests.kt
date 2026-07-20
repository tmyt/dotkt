// Default-argument battery (M2 batch) — migrates the default-argument family of cases/il-* onto the in-process
// NUnit suite. Each old case's `main` + stdout-golden diff becomes one @TestAttribute method whose per-value
// assertEquals is strictly stronger (typed) than the old text diff; every asserted value is preserved 1:1
// (see the `// <expected>` comments). Top-level declarations are `M2`-prefixed (one assembly = one namespace).
//
// Coverage preserved (old case -> method):
//   il-defargs  -> m2_defargs   C3: cross+same-module default args; an omitted middle default must not shift a
//                                later provided arg's slot (joinToString / substringAfter `= this` / copy(field=))
//   il-defargs2 -> m2_defargs2  same-module default referencing ANOTHER value param (`b = a*10`, `c = a+b`)
//
// The data class was `P`; renamed `M2P` for collision-freedom, so its toString reads `M2P(...)` — the class name is
// incidental to the subject (default-arg slot correctness), which is unchanged.
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

data class M2P(val x: Int, val y: Int, val z: Int)

fun m2Greet(name: String, greeting: String = "Hello", punct: String = "!"): String = "$greeting, $name$punct"

fun m2F(a: Int, b: Int = a * 10): Int = a + b
fun m2G(x: Int, y: Int = x + 1): Int = x * y
fun m2H(a: Int, b: Int = 3, c: Int = a + b): Int = a * 100 + b * 10 + c

class MigratedM2DefaultArgsTests {
    @TestAttribute
    fun m2_defargs() {
        val xs = listOf(1, 2, 3)
        assertEquals("x1-x2-x3", xs.joinToString("-") { "x$it" })                          // x1-x2-x3
        assertEquals("1, 2, 3", xs.joinToString())                                         // 1, 2, 3
        assertEquals("[1, 2, 3]", xs.joinToString(prefix = "[", postfix = "]"))            // [1, 2, 3]
        assertEquals("1/2/~", xs.joinToString(separator = "/", limit = 2, truncated = "~")) // 1/2/~
        assertEquals("b=c", "a=b=c".substringAfter("="))                                   // b=c
        assertEquals("a", "a=b=c".substringBefore("="))                                    // a
        assertEquals("nodelim", "nodelim".substringAfter("="))                             // nodelim
        assertEquals("FALLBACK", "nodelim".substringBefore("=", "FALLBACK"))               // FALLBACK
        val p = M2P(1, 2, 3)
        assertEquals("M2P(x=1, y=20, z=3)", p.copy(y = 20).toString())                     // was P(x=1, y=20, z=3)
        assertEquals("M2P(x=10, y=2, z=30)", p.copy(x = 10, z = 30).toString())            // was P(x=10, y=2, z=30)
        assertEquals("Hello, Kotlin!", m2Greet("Kotlin"))                                  // Hello, Kotlin!
        assertEquals("Hello, Kotlin?", m2Greet("Kotlin", punct = "?"))                     // Hello, Kotlin?
    }

    @TestAttribute
    fun m2_defargs2() {
        assertEquals(55, m2F(5))        // 55
        assertEquals(7, m2F(5, 2))      // 7
        assertEquals(12, m2G(3))        // 12
        assertEquals(30, m2G(3, 10))    // 30
        assertEquals(134, m2H(1))       // 134
        assertEquals(156, m2H(1, 5))    // 156
        assertEquals(159, m2H(1, 5, 9)) // 159
    }
}
