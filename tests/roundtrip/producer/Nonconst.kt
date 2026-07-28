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
// #235: a base whose constructor default a SUBCLASS omits at its `: super(…)` — a delegation is a call site too, and
// cross-module its args ride the subclass's constructor declaration rather than a call node.
open class Panel2(val w: Int, val h: Int = w * 2) { val area: Int get() = w * h }
// Both defaults omitted there, the FIRST side-effecting and the SECOND reading it: the filled `a` must be evaluated
// once and read from a temp by `b`, which needs the target ctor's declared parameter types (a delegation carries none).
var seeds: Int = 0
fun seed(): Int { seeds++; return 3 }
open class Seeded(val a: Int = seed(), val b: Int = a * 10)
// ...and one that also takes a SUPPLIED leading argument, so the delegation's ORDER is observable: Kotlin evaluates the
// value the `: super(…)` supplies before any of the callee's defaults. A delegation's arguments ride the constructor
// DECLARATION, so that order is carried by the plan's `preStmts` rather than by an expression.
var seedOrder: String = ""
fun seedMarkP(): Int { seedOrder += "p"; return 2 }
fun seedMarkD(): Int { seedOrder += "d"; return 3 }
open class SeededOrder(val p: Int, val a: Int = seedMarkD(), val b: Int = a * 10)
// #235: an UNSIGNED parameter beside a class-typed sibling of the same arity. `UInt` is `kotlin.UInt` at a call site and
// `System.UInt32` in a reference assembly; unless the signature key folds the two spellings, one overload's key collapses
// onto the other's and the wrong default is spliced.
class Marker(val s: String)
fun uf(a: UInt, b: Int = 1): String = "u$a/$b"
fun uf(a: Marker, b: Int = 2): String = "m${a.s}/$b"
// Two SAME-ARITY ctors both carrying a non-constant default: the splice key carries the declared parameter vector, so
// the omitting consumer resolves the right overload instead of whichever the metadata scan enumerated last.
class Pair2(val n: Int, val label: String = n.toString() + "!") {
    constructor(s: String, upper: String = s.uppercase()) : this(upper.length)
}

// #235 SINGLE EVALUATION: each of these has a carrier that reads a value of the CALL — the extension receiver, an
// argument, an argument read by TWO defaults, an argument no default reads (order), and a side-effecting DEFAULT that a
// later default reads. The consumer counts how many times its own side-effecting expression runs.
fun String.suffixed(t: String = this): String = this + "/" + t
// #235: SAME-ARITY overloads of one name carrying DIFFERENT defaults. The carrier is keyed by the declared parameter
// vector, so each call site resolves its own default instead of the arity key serving whichever declaration the metadata
// scan reached first. Several pairs, because they key differently: `ov`'s parameters fold to the same token on both
// sides (`i32`/`str`); `tagged`'s sibling differs in a CLASS position (`List<String>` against `String`), comparable only
// through the relaxed key; `note`'s differs in a NULLABLE REFERENCE position; and `uf`'s in an UNSIGNED one.
fun ov(a: Int, b: Int = a * 2): String = "$a/$b"
fun ov(a: String, b: String = a + "!"): String = "$a/$b"
// arity 2 as well, because an extension's receiver rides as a leading `__self` parameter — the exact collision that
// broke this lane when the carrier was keyed by name+arity alone.
fun String.tagged(t: String = this): String = this + "/" + t
// A third pair, differing in a NULLABLE REFERENCE parameter: `String?` lowers to a plain `System.String` (its
// nullability rides [Nullable]), so the reference side reads that position without the wrapper the call side carries.
fun note(msg: String, tag: String? = null): String = "$msg/${tag ?: "-"}"
fun note(msg: Int, tag: Int = 7): String = "$msg/$tag"
fun scaled(a: Int, b: Int = a * 10): Int = a + b
fun tri3(a: Int, b: Int = a + 1, c: Int = a * 100 + b): Int = c
fun order3(p: Int, q: Int, r: Int = q * 10): String = "$p/$q/$r"
var bumps: Int = 0
fun bump(): Int { bumps++; return 3 }
fun chain(a: Int, b: Int = bump(), c: Int = b * 10): Int = b * 1000 + c
