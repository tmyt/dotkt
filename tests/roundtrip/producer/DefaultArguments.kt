// Migrated verify-roundtrip.sh section `roundtrip-defargs` (#134) — the library half.
// A restored default arg carries a REAL constant value (`opt:Type=<const>` metadata -> a FirLiteralExpression),
// so the consumer can omit it ANYWHERE: trailing, NAMED-MIDDLE (skip a middle default, provide a later one —
// which @JvmOverloads positional overloads could NOT express), or reordered named. Constructors too (ilemit
// emits ctor parameter NAMES). String defaults with spaces + a nullable `= null` default survive.
package roundtrip.defaultarguments

fun greet(name: String, greeting: String = "Hi", punct: String = "!"): String = "$greeting, $name$punct"
fun box(a: Int, b: Int = 2, c: Int = 3): Int = a * 100 + b * 10 + c
fun flags(on: Boolean = true, label: String = "x y"): String = "$on/$label"
// non-Int kinds + a NULLABLE (`= null`) default, to lock every metaConstArg kind + the null-literal path
fun kinds(tag: String, n: Long = 5L, r: Double = 1.5, ch: Char = 'z', note: String? = null): String =
    "$tag/$n/$r/$ch/${note ?: "none"}"
class Pt(val x: Int = 0, val y: Int = 0) { override fun toString(): String = "($x,$y)" }

open class InheritedBase<T>(private val seed: T) {
    fun inheritedValue(): T = seed
    open fun describe(value: T = inheritedValue(), tail: String = "$value!"): String = "base:$value/$tail"
}
open class InheritedMiddle<T>(seed: T) : InheritedBase<T>(seed) {
    override fun describe(value: T, tail: String): String = "middle:$value/$tail"
}
class InheritedLeaf(seed: String) : InheritedMiddle<String>(seed)

open class InheritedMappedMiddle<T>(seed: T) : InheritedBase<List<T>>(listOf(seed)) {
    override fun describe(value: List<T>, tail: String): String = "mapped:${value.first()}/$tail"
}
class InheritedMappedLeaf(seed: String) : InheritedMappedMiddle<String>(seed)
