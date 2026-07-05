// CharSequence.windowed — the overload whose body uses a `break` in EXPRESSION position
// (`val coercedEnd = if (...) ... else break`). The sibling Iterable.windowed already worked;
// this exercises kotc's break-in-expression lowering (a valueBlock + goto/break + unreachable throw).
fun main() {
    println("abcd".windowed(2))                 // [ab, bc, cd]
    println("abcde".windowed(2, 2))             // [ab, cd]
    println("abcde".windowed(3, 1, true))       // [abc, bcd, cde, de, e]
    println("abcdef".windowed(2, 3))            // [ab, de]
    println("abcd".windowed(2) { it.toString() }) // [ab, bc, cd]  (reference-typed transform)
}
