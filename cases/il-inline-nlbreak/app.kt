// #75 S4a §8.3 — non-local break from inside forEach to an OUTER `for` loop, exercising §4.1 hygiene THROUGH a
// carrier. The break's `loop` is outside forEach's lambda region -> ESCAPE -> forEach splices; the break becomes a
// `{k:goto,<caller-outer-loop-label>}` inside the carried lambda body. The §4.1 fix must NOT re-mint that goto's id
// (its matching label lives OUTSIDE the freshened region) — the old collect-goto-ids code would dangle it. The
// local `return@forEach` in the same lambda IS routed to forEach's own end-label (a label inside the region).
fun main() {
    outer@ for (x in 1..3) {
        listOf(10, 20, 30).forEach {
            if (it == 20) return@forEach          // local continue-like
            if (x == 2 && it == 30) break@outer   // non-local break to the outer for
            println("$x:$it")
        }
    }
    println("done")
}
