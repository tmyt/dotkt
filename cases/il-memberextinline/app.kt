// #20: an inline MEMBER-extension fn (a member of the companion AND an extension on Long) called with a lambda via
// `receiver.fn { ... }`. The callee has BOTH a dispatch receiver (the companion, unused here) and an extension receiver
// (the Long `this`). kotc must splice it: the extension receiver rides `__self`, the lambda is inlined at `block(...)`.
// The `firstNonZero` block does a NON-LOCAL return, which requires the fn to stay `inline` (it cannot be de-inlined).
class Queue {
    companion object {
        inline fun <T> Long.withState(block: (head: Int, tail: Int) -> T): T {
            val head = (this shr 8).toInt()
            val tail = (this and 0xFF).toInt()
            return block(head, tail)
        }
    }
    fun sum(state: Long): Int = state.withState { h, t -> h + t }
    fun firstNonZero(state: Long): Int {
        state.withState { h, t ->
            if (h != 0) return h   // non-local return from firstNonZero, through the inlined member-extension
            if (t != 0) return t
        }
        return -1
    }
}

fun main() {
    val q = Queue()
    println(q.sum(0x0102))          // head=1, tail=2 -> 3
    println(q.firstNonZero(0x0102)) // head=1 != 0 -> 1
    println(q.firstNonZero(0x0002)) // head=0, tail=2 -> 2
    println(q.firstNonZero(0x0000)) // both 0 -> -1
}
