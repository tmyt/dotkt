// bir2cir SuspendColdLowering — yieldAll overload resolution (BUG Y). SequenceScope.yieldAll has
// three suspend overloads whose cold entries share the name `yieldAll$dotkt_suspend`; the ColdCall
// rewrite must carry a `sig` so ilemit's MethodsBySig picks the right overload (Iterable here).
fun main() {
    val s = sequence {
        yield("a")
        yieldAll(listOf("b", "c"))
    }
    println(s.toList().joinToString(","))   // a,b,c
}
