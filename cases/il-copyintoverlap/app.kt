// #97: Array.copyInto MUST be overlap-safe (memmove via System.Array.Copy), not a clobbering forward
// element loop. A self-copy with destinationOffset != startIndex overlaps; a forward loop overwrites
// source slots before reading them, replicating an element. ArrayDeque shifts in place via the generic
// (Array<E>) copyInto, so ArrayDeque.add(index, elem) silently corrupted the deque without the fix.
// (The 8 primitive-array copyInto actuals are fixed identically; they are not exercised directly here
// because app-level resolution of a primitive-array-receiver stdlib extension is a separate pre-existing
// bir2cir/ilemit gap — `intArrayOf(...).copyInto/toList/...` -> "static method not found".)

fun main() {
    // generic Array<T>, right shift (destinationOffset > startIndex): copy [0..4) to offset 1
    val a = arrayOf(1, 2, 3, 4, 5)
    a.copyInto(a, 1, 0, 4)
    println(a.joinToString(","))       // 1,1,2,3,4

    // generic Array<T>, left shift (destinationOffset < startIndex): copy [1..5) to offset 0
    val b = arrayOf(1, 2, 3, 4, 5)
    b.copyInto(b, 0, 1, 5)
    println(b.joinToString(","))       // 2,3,4,5,5

    // reference Array<String>, right shift by 2 — a clobbering forward loop would yield a,b,a,b,a
    val s = arrayOf("a", "b", "c", "d", "e")
    s.copyInto(s, 2, 0, 3)
    println(s.joinToString(","))       // a,b,a,b,c

    // real-world victim: ArrayDeque middle insertion is an in-place overlapping shift (generic copyInto)
    val dq = ArrayDeque<String>(10)
    dq.addAll(listOf("a", "b", "c", "d"))
    dq.add(2, "X")
    println(dq.joinToString(","))      // a,b,X,c,d
}
