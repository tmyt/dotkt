// Producer surface part 3: interface + default method + inheritance + virtual dispatch, generics, and
// nullable types. Nullable REFERENCE params/returns and nullable value-type RETURNS re-import cleanly; a
// nullable value-type generic in PARAM/FIELD position is a known cross-module gap (#86/#147) — deliberately
// NOT exercised here and mapped as a stays-broken item in the design doc.
package roundtrip.api

interface Shape {
    fun area(): Int
    fun describe(): String = "shape area=" + area()   // default interface method
}
// Rect: a DIRECT child overriding both the abstract area() and the default describe() (both round-trip cleanly).
open class Rect(val w: Int, val h: Int) : Shape {
    override fun area(): Int = w * h
    override fun describe(): String = "rect area=" + area()
}
// Square: overrides area() only and INHERITS Rect.describe(). Calling describe() on a Square must run Rect's
// override with area() dispatching to Square's -> proves polymorphic dispatch across the dll. (NB: a subclass
// that itself re-overrides an interface DEFAULT method through a non-overriding intermediate is a separate
// compiler bug surfaced by this migration — see docs/design-nunit-test-harness.md "Surfaced bugs".)
class Square(side: Int) : Rect(side, side)

// generic class + generic function with a nullable value-type RETURN (works via method-return carrier)
class Wrap<T>(val value: T) { fun unwrap(): T = value }
fun <T> firstOrNull2(xs: List<T>): T? = if (xs.isEmpty()) null else xs[0]

// nullable reference-type param
fun lengthOr(s: String?, fallback: Int): Int = s?.length ?: fallback
