// Migrated verify-roundtrip.sh section `roundtrip-pkg` — the library half (geom package).
// Kotlin packages project to .NET namespaces, consumed via package-qualified imports. Also guards the
// correctness bug where same-named classes in different packages collided at the root namespace (see the
// sibling Pkgother.kt `Vec`). Covers: namespace; reified inline -> generic method; cross-module inline +
// non-local return; properties (custom getter / mutable write); top-level extension operator + extension
// property; vararg; default argument; nullable parameter.
package roundtrip.pkg

enum class Dir { NORTH, EAST }
class Vec(var x: Int, var y: Int) {
    infix fun dot(o: Vec): Int = x * o.x + y * o.y
    val mag2: Int get() = x * x + y * y          // property with a custom getter
}
operator fun Vec.plus(o: Vec): Vec = Vec(x + o.x, y + o.y)   // top-level extension operator
val Vec.manhattan: Int get() = x + y                          // extension property
fun sumAll(vararg xs: Int): Int { var s = 0; for (v in xs) s += v; return s }   // vararg
fun tagged(s: String = "def"): String = s                    // default argument
fun orNone(s: String?): String = s ?: "none"                 // nullable parameter
fun greet(name: String): String = "Hi, " + name
// A physical parameter name is not a Kotlin receiver role. dll2klib must keep this as an ordinary two-argument
// function even though the first slot deliberately uses kotc's internal extension-receiver spelling.
fun ordinarySelfName(__self: Int, delta: Int): Int = __self + delta
inline fun Int.receiverNameCollision(__self: Int, block: (Int) -> Int): Int = block(this) + __self
inline fun <reified T> typeName(): String = T::class.simpleName ?: "?"   // reified inline -> generic method
inline fun forEach3(a: Int, b: Int, c: Int, action: (Int) -> Unit) { action(a); action(b); action(c) }
