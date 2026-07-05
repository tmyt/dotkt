// C3 family (kcc review 2026-07-05 §2A): cross-module AND same-module default arguments. A middle-omitted default
// must not shift a later provided arg into the wrong parameter slot. JVM-oracle PURE — real Kotlin is the ground truth.
data class P(val x: Int, val y: Int, val z: Int)

// A same-module function with constant defaults, exercised with a named-arg gap (greeting omitted, punct provided).
fun greet(name: String, greeting: String = "Hello", punct: String = "!"): String = "$greeting, $name$punct"

fun main() {
    val xs = listOf(1, 2, 3)
    // joinToString: separator provided + trailing transform lambda, all four middle defaults omitted.
    println(xs.joinToString("-") { "x$it" })
    println(xs.joinToString())
    println(xs.joinToString(prefix = "[", postfix = "]"))
    println(xs.joinToString(separator = "/", limit = 2, truncated = "~"))
    // substringAfter/Before: missingDelimiterValue = this (receiver-referencing default), found + not-found branches.
    println("a=b=c".substringAfter("="))
    println("a=b=c".substringBefore("="))
    println("nodelim".substringAfter("="))
    println("nodelim".substringBefore("=", "FALLBACK"))
    // data class copy(field = ...): the generated y = this.y (receiver-referencing) default, same-module.
    val p = P(1, 2, 3)
    println(p.copy(y = 20))
    println(p.copy(x = 10, z = 30))
    // same-module constant defaults with a named-arg gap.
    println(greet("Kotlin"))
    println(greet("Kotlin", punct = "?"))
}
