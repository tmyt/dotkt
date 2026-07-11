// #75 S4a §8.1 — a { b { return } }: THE predicate trap. The bare `return` inside the INNER inline-arg lambda
// targets the CALLER (main). The escape predicate must DESCEND into the outer forEach lambda's nested inline forEach
// arg to SEE that return -> both forEach calls splice -> the caller returns. A direct-arg-only predicate sees no
// return in the outer lambda's own statements, delegate-compiles the outer forEach, and SILENTLY DROPS the return.
// Proof: "after" must NOT print.
fun main() {
    listOf(1, 2, 3).forEach { a ->
        listOf(10, 20).forEach { b ->
            if (a == 2 && b == 20) {
                println("hit $a $b")
                return
            }
        }
    }
    println("after")
}
