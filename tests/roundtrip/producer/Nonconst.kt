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

// #235 SINGLE EVALUATION: each of these has a carrier that reads a value of the CALL — the extension receiver, an
// argument, an argument read by TWO defaults, an argument no default reads (order), and a side-effecting DEFAULT that a
// later default reads. The consumer counts how many times its own side-effecting expression runs.
// `suffixed` deliberately shares neither name nor arity with `tagged` above; the same-arity OVERLOAD pair that exercises
// the signature-keyed carrier lookup is `ov` below.
fun String.suffixed(t: String = this): String = this + "/" + t
// #235: two SAME-ARITY overloads of one name carrying DIFFERENT defaults. The carrier is keyed by the declared parameter
// vector, so each call site resolves its own default instead of whichever declaration the metadata scan reached last.
fun ov(a: Int, b: Int = a * 2): String = "$a/$b"
fun ov(a: String, b: String = a + "!"): String = "$a/$b"
fun scaled(a: Int, b: Int = a * 10): Int = a + b
fun tri3(a: Int, b: Int = a + 1, c: Int = a * 100 + b): Int = c
fun order3(p: Int, q: Int, r: Int = q * 10): String = "$p/$q/$r"
var bumps: Int = 0
fun bump(): Int { bumps++; return 3 }
fun chain(a: Int, b: Int = bump(), c: Int = b * 10): Int = b * 1000 + c
