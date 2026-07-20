// Migrated verify-roundtrip.sh section `roundtrip-nothing-return` (#133) — the library half.
// A Kotlin `Nothing` return round-trips: the consumer's `if/else` with a Nothing branch keeps the
// non-Nothing type (no Any? widening). `pick` also exercises Nothing INSIDE a generic top-level fn.
package roundtrip.nothingret

fun fail(msg: String): Nothing = throw RuntimeException(msg)
fun <T> pick(cond: Boolean, x: T): T = if (cond) x else fail("no")
