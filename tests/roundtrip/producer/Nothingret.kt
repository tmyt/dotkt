// Migrated verify-roundtrip.sh sections `roundtrip-nothing-return` (#133) and `roundtrip-nothing` (#135/#197) —
// the library half. A Kotlin `Nothing` return round-trips: the consumer's `if/else` with a Nothing branch keeps
// the non-Nothing type (no Any? widening). `pick` also exercises Nothing INSIDE a generic top-level fn, and
// `Boom.boom` the COMPANION-STATIC return the dll2klib companion-static loop reads (#135).
package roundtrip.nothingret

fun fail(msg: String): Nothing = throw RuntimeException(msg)
fun <T> pick(cond: Boolean, x: T): T = if (cond) x else fail("no")

class Boom {
    companion object { fun boom(): Nothing = throw RuntimeException("boom") }   // companion-static Nothing (#135)
}
