// #199-① regression (roundtrip lane), half A. Two GENERIC types sharing a simple name (`Cell`) in DIFFERENT packages
// of ONE producer assembly (this file = package a; GenclashB.kt = package b). dll2klib must emit every
// reference to `Cell` (a generic-factory RETURN, a `var` PROPERTY type, a generic supertype) as its NAMESPACE-QUALIFIED
// name so a factory's return and a var's type resolve to the correct package.
// Consumed cross-module by RoundtripTests.genericSameSimpleNameAcrossPackages via the built dll (NOT source).
package roundtrip.genclash.a

class Cell<T>(var value: T) { fun boxed(): T = value }

fun <T> cellA(v: T): Cell<T> = Cell(v)
