// Nullable-operator battery (batch MigM, from cases/m-s1) — the Elvis `?:`, not-null assert `!!`, and safe-call
// `?.` on nullable String. Migrated onto the in-process NUnit suite; each old case's `main` + golden becomes one
// @TestAttribute method whose per-value assert is strictly stronger (typed) than the old stdout diff; every
// asserted value preserved 1:1 (see `// <expected>`). Formerly DOUBLE-registered (verify-il il_check `nullv`
// AND verify-differential PURE `m-s1`) — both registrations removed in this same change.
//
// Coverage preserved (old case -> method):
//   m-s1  -> nullableOperators   `?:` (Elvis) / `!!` (not-null assert) / `?.` (safe call) chained with `?:`
//
// Top-level names are MigM-prefixed (one assembly = one namespace).
import NUnit.Framework.TestAttribute
import NUnit.Framework.Legacy.ClassicAssert.Companion.AreEqual as assertEquals

fun migmPick(a: String?, b: String): String = a ?: b
fun migmForce(a: String?): String = a!!
fun migmLenOr(a: String?): Int = a?.length ?: -1

class MigMNullableTests {
    @TestAttribute
    fun nullableOperators() {
        assertEquals("fallback", migmPick(null, "fallback"))  // fallback (Elvis takes RHS on null)
        assertEquals("present", migmPick("present", "fallback"))  // present (Elvis keeps LHS)
        assertEquals("forced", migmForce("forced"))           // forced  (!! passes on non-null)
        assertEquals(-1, migmLenOr(null))                     // -1  (?. short-circuits, Elvis -> -1)
        assertEquals(5, migmLenOr("hello"))                   // 5   (?. reaches .length)
    }
}
