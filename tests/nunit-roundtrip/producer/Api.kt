// Producer surface part 1: top-level function + top-level property (KotlinFileClass), overloads, default
// args, extension function, inline function — the Kotlin modifiers with no direct .NET analog that must
// survive the emit->re-import round-trip.
package roundtrip.api

// top-level property + top-level function (restored from [KotlinFileClass]/[KotlinFunction])
val libraryName: String = "roundtrip-producer"
fun topLevelGreeting(who: String): String = "hello, $who"

// overloads — both must be visible + resolvable across the dll
fun combine(a: Int, b: Int): Int = a + b
fun combine(a: String, b: String): String = a + b

// default arguments (the default value must be re-imported, not lost)
fun withDefaults(base: Int, step: Int = 10): Int = base + step

// extension function (receiver -> __self first param; restored as an extension on re-import)
fun String.echoTwice(): String = this + this

// inline function (must remain inline across the module boundary)
inline fun applyTwice(x: Int, f: (Int) -> Int): Int = f(f(x))
