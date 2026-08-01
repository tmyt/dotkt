// #86 — the crossing reached only through a KOTLIN interface declared here.
//
// `KThrough` itself is fine and must keep compiling: an interface inherits the obligation without discharging it,
// emits no body, and is pinned as such in tests/interop. The CLASS is the one that must fill the slot, and it
// reaches the declaration only by way of a local hop — so the walk has to cross provenances rather than stop at
// the first supertype declared in this compilation.
import plainnet.ITakeThrough
import System.Collections.Generic.List

interface KThrough : ITakeThrough

class CThroughKotlin : KThrough {
    override fun Take(xs: List<Int?>): String = "K:ok"
}

fun main() {
    println(CThroughKotlin().toString().substring(0, 2))
}
