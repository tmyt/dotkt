// generateSequence — value-T and reference-T variants (chosen by the compiler; `(T)->T?` has different CLR shapes).
fun main() {
    // value T (Int): yield 1,2,3,4,5 then null stops.
    val s = generateSequence(1) { if (it < 5) it + 1 else null }
    println(s.toList().joinToString(","))             // 1,2,3,4,5

    // reference T (String): grow until length 3.
    val s2 = generateSequence("a") { if (it.length < 3) it + "a" else null }
    println(s2.toList().joinToString(","))            // a,aa,aaa

    // laziness + the seedless form: take short-circuits an infinite generator.
    var n = 0
    val s3 = generateSequence { n = n + 1; n }
    println(s3.take(3).toList().joinToString(","))     // 1,2,3
}
