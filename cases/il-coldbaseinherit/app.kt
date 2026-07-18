// R1 (#90) — a BASE-CLASS-declared suspend fun called via a SUBCLASS receiver, WITHOUT an override. `FastReader`
// inherits `read()` from `Reader`; the suspend call `r.read()` is keyed on FastReader but the cold entry
// `read$dotkt_suspend` is declared on Reader. Under R1 the rewrite is UNCONDITIONAL and native virtual dispatch
// through Reader's (open) virtual cold slot resolves the inherited member — no bir2cir hierarchy walk. The old
// resolvability fixpoint dropped this shape (or leaned on the deleted AllSupers walk).
import dotkt.support.blockOn

open class Reader(val seed: Int) {
    open suspend fun read(): Int = seed + 1
}
class FastReader(seed: Int) : Reader(seed)

suspend fun drive(r: FastReader): Int = r.read()

fun main() {
    println(blockOn { drive(FastReader(41)) })   // 42
}
