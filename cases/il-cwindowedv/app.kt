// CharSequence.windowed with a VALUE-TYPE transform result (R = Int / Char). The transform lambda is a
// `delegateNew` target whose funcType keeps the synthetic `<>dotkt_CharSequence` (matching the stdlib's
// `Func<CharSequence, R>` generic sig), so its `it` param must NOT be collapsed to `System.String` by the
// pure-app CharSequence->String lowering — the stdlib passes a genuine `<>dotkt_CharSequence` (subSequence's
// result) into it. Regression guard for W4-B: value-R garbled to pointer bits when the box/typing was wrong.
fun main() {
    println("abcd".windowed(2) { it.length })     // [2, 2, 2]
    println("abcd".windowed(2) { it[0] })         // [a, b, c]
    println("abcde".windowed(3) { it.length })    // [3, 3, 3]
    println("abcd".windowed(2) { it.toString() }) // [ab, bc, cd]  (reference-typed transform stays correct)
}
