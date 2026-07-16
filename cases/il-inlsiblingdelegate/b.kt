// F4 (#63) file B: an inline fn whose body materializes a CAPTURE-LESS nested lambda into a `newDelegate`
// targeting a `__lambdaN` lifted into B's FILE class. `bPick` carries a lambda param (=> splice-able as a
// callInline), and its else-branch forwards `{ it + 100 }` (no captures) to the non-inline `bSink` — kotc
// lifts that lambda to `__lambdaN` in B's file class + a `newDelegate`. When A splices `bPick` inside a
// materialized carrier, that `newDelegate` lands in the carrier body; its provenance is a SIBLING file, not A.
fun bSink(n: Int, g: (Int) -> Int): Int = g(n)
inline fun bPick(cond: Boolean, primary: () -> Int): Int =
    if (cond) primary() else bSink(7) { it + 100 }
