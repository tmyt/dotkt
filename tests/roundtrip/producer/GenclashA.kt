// #199-① regression (roundtrip lane), half A. Two GENERIC types sharing a simple name (`Cell`) in DIFFERENT packages
// of ONE producer assembly (this file = package a; GenclashB.kt = package b). On re-import facadegen must emit every
// reference to `Cell` (a generic-factory RETURN, a `var` PROPERTY type, a generic supertype) as its NAMESPACE-QUALIFIED
// name — a BARE `Cell` collapses both packages' types to one (the injector's by-simple-name map is last-put-wins), so a
// factory's return / a var's type resolves to the WRONG package's `Cell` and its `var` mutability + members degrade.
// Consumed cross-module by RoundtripTests.genericSameSimpleNameAcrossPackages via the built dll (NOT source).
package roundtrip.genclash.a

class Cell<T>(var value: T) { fun boxed(): T = value }

fun <T> cellA(v: T): Cell<T> = Cell(v)
