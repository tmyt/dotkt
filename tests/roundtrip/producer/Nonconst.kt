// Migrated verify-roundtrip.sh section `roundtrip-nonconst-default` (#146) — the library half.
// #134 carried a CONSTANT default as a metadata value; #146 extends the SAME @KotlinDefault mechanism to a
// NON-CONST default — an empty receiver lambda `= {}` (the Avalonia DSL idiom), a plain empty lambda, and a
// simple-expression default `= emptyList()`. kotc carries the default as a CLOSED BIR sub-tree; facadegen marks
// the injected param OPTIONAL (nonConst); bir2cir's DefaultArgSplice fills the omitted slot cross-module.
package roundtrip.nc

class Panel { var margin: Int = 0; fun add(s: String): Int { margin += s.length; return margin } }
fun column(configure: Panel.() -> Unit = {}, build: Panel.() -> Unit): Int { val p = Panel(); p.configure(); p.build(); return p.margin }
fun run2(pre: () -> Unit = {}, body: () -> Unit): String { pre(); body(); return "ok" }
fun tagged(name: String, items: List<String> = emptyList()): String = "$name=${items.size}"

// #235: the CONSTRUCTOR half of the same mechanism. A ctor's non-constant default now carries `@KotlinDefault` too,
// so a consumer can omit it: reading an EARLIER ctor param, a CHAIN (the second default reads the first, itself
// filled), and a call (`= emptyList()`) mixed with a metadata-representable constant in a later slot.
class Rect(val w: Int, val h: Int = w * 2, val tag: String = "r") { val area: Int get() = w * h }
class Tri(val a: Int, val b: Int = a + 1, val c: Int = a * 100 + b)
class Bag(val items: List<String> = emptyList(), val n: Int = 1) { val size: Int get() = items.size * 10 + n }
// Two SAME-ARITY ctors both carrying a non-constant default: the splice key carries the declared parameter vector, so
// the omitting consumer resolves the right overload instead of whichever the metadata scan enumerated last.
class Pair2(val n: Int, val label: String = n.toString() + "!") {
    constructor(s: String, upper: String = s.uppercase()) : this(upper.length)
}
