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
