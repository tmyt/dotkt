// Migrated verify-roundtrip.sh section `roundtrip-toplevel-val` (#34b) — the library half.
// A top-level `val`/`var` compiles to a plain Public|Static FIELD on the file class (with NO get_/set_
// accessor). facadegen surfaces each such field as a `tlprop` meta token so a consumer reads the property
// DIRECTLY (`import roundtrip.tlval.greeting`), not through a re-exposing function. Cases: a `val: String`,
// a `var: Int` (read + write `+=`), and a `val` of a USER type (Point).
package roundtrip.tlval

class Point(val x: Int, val y: Int) { override fun toString(): String = "($x, $y)" }
val greeting: String = "hi"       // top-level val -> static field, read cross-module directly
var counter: Int = 40             // top-level var -> read + write cross-module
val origin: Point = Point(1, 2)   // top-level val of a USER type
