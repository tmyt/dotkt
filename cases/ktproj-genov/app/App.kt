// #25 APP: consumes the re-imported `kotlinx.genov` overload set through a <ProjectReference>. `atomic<String?>(null)`
// must resolve to the ARITY-1 generic `atomic(T): Ref<T>` (tag "gen1") — not the arity-2 defaulted sibling
// (which would tag "gen2:…") — and the sole-generic `arrOf<String>(3)` must be found. Before the fix the generic
// `callStatic` reached ilemit WITHOUT a resolved sig, so overload resolution mis-bound `atomic` and could not find `arrOf`.
import kotlinx.genov.Ref
import kotlinx.genov.Arr
import kotlinx.genov.atomic
import kotlinx.genov.arrOf

fun main() {
    val arr = arrOf<String>(3)           // sole-generic array factory
    println(arr.size)                    // 3
    val a = atomic<String?>(null)        // generic arity-1: tag "gen1"
    println(a.tag)                       // gen1
    val b = atomic(42)                   // non-generic Int overload: tag "int"
    println(b.tag)                       // int
}
