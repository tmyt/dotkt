// Declaration-shape battery (batch MigM, from cases/m-a4 + m-a6) — migrates the pure-Kotlin declaration cases of
// the cases/m-* differential corpus onto the in-process NUnit suite. Each old case's `main` + JVM-oracle golden
// becomes one @TestAttribute method whose per-value assert is strictly stronger (typed) than the old stdout diff;
// every asserted value is preserved 1:1 (see the `// <expected>` comments).
//
// Coverage preserved (old case -> method):
//   m-a4  -> topLevelDeclsVarargWhen   top-level const val / val / var (static field), vararg -> params, subjectless `when`
//   m-a6  -> companionConstValFactory  companion object: const (inlined), non-const val (static field), factory method (static)
//
// All top-level declarations here are MigM-prefixed (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

// ---- m-a4 : top-level properties (const val / val / var), vararg, subjectless `when` --------------------------
const val MIGM_GREETING = "hi"
val MIGM_MAX = 100
var migmCounter = 0

fun migmSumAll(vararg xs: Int): Int {
    var s = 0
    for (x in xs) s += x
    return s
}

fun migmGrade(n: Int): String = when {   // subjectless `when`
    n >= 90 -> "A"
    n >= 80 -> "B"
    else -> "C"
}

// ---- m-a6 : companion object — const (inlined), non-const val (static field), factory method (static) ---------
class MigMCircle(val r: Double) {
    companion object {
        const val PI = 3.14
        val NAME = "circle"
        fun unit(): MigMCircle = MigMCircle(1.0)
    }
    fun area(): Double = PI * r * r
}

class MigMDeclTests {
    @TestAttribute
    fun topLevelDeclsVarargWhen() {
        assertEquals("hi", MIGM_GREETING)      // hi   (const val)
        assertEquals(100, MIGM_MAX)            // 100  (val, static field)
        migmCounter = 0
        migmCounter = migmCounter + 5
        assertEquals(5, migmCounter)           // 5    (var, static field mutated)
        assertEquals(10, migmSumAll(1, 2, 3, 4))  // 10 (vararg -> params)
        assertEquals("B", migmGrade(85))       // B    (subjectless when)
        assertEquals("A", migmGrade(95))       // A
        assertEquals("C", migmGrade(70))       // C
    }

    @TestAttribute
    fun companionConstValFactory() {
        assertEquals(3.14, MigMCircle.PI)      // 3.14   (companion const)
        assertEquals("circle", MigMCircle.NAME)  // circle (companion non-const val = static field)
        val u = MigMCircle.unit()              // factory method (static)
        assertEquals(3.14, u.area())           // PI * 1.0 * 1.0 = 3.14
    }
}
