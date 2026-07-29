// Migrated verify-roundtrip.sh section `roundtrip-toplevel-val` (#195) — the library half.
// A bare top-level `val greeting = "hi"` with NO custom accessor compiles (kotc) to a plain Public|Static
// FIELD on the file class (`roundtrip.tlval.TlvalKt`), with NO get_/set_ accessor (only backing-field-LESS
// props — extension/computed — get accessors). dll2klib now surfaces each such field from the BUILT dll's
// [KotlinFileClass] so `import roundtrip.tlval.greeting` resolves against the field DIRECTLY — the #195
// The reference KLIB must include fields regardless of which source imports reach them.
// Cases: a `val: String`, a `var: Int` (read + cross-module write `+=`), and a `val` of a USER type.
package roundtrip.tlval

class TlPoint(val x: Int, val y: Int) { override fun toString(): String = "($x, $y)" }

val greeting: String = "hi"          // top-level val -> plain static field, read cross-module directly
var counter: Int = 40                // top-level var -> read + cross-module write
val origin: TlPoint = TlPoint(1, 2)  // top-level val of a USER type
