// R1 (#90) — a DEFAULTED generic-interface suspend method (a DIM), the `Channel<E>.receiveOrNull` shape. `Source<E>`
// declares an abstract `fetch()` and a DEFAULT `fetchOrDefault()` that suspend-calls `fetch()` through `this`. The
// default is a CONCRETE member of a GENERIC interface (Continuation<object> erasure): it segments into an SM with a
// `$this` of type Source<E>, and its `this.fetch()` cold call dispatches virtually to the IntSource override. The
// old code either dropped the default (unresolvable interface callee) or refused the generic-interface member.
import dotkt.support.blockOn

interface Source<E> {
    suspend fun fetch(): E
    suspend fun fetchOrDefault(fallback: E): E {
        val v = fetch()
        return v
    }
}
class IntSource(val v: Int) : Source<Int> {
    override suspend fun fetch(): Int = v
}
suspend fun drive(s: Source<Int>): Int = s.fetchOrDefault(0)

fun main() {
    println(blockOn { drive(IntSource(42)) })   // 42
}
