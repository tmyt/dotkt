// Real inlining of lambda-param inline funs (function-inlining-spike): non-local return + value inline.
// (lambda-LESS inline funs are NOT inlined — emitted as ordinary methods for the JIT to inline.)

// Unit-returning inline fun + NON-LOCAL return from the lambda (exits the ENCLOSING fun).
inline fun forEach3(a: Int, b: Int, c: Int, action: (Int) -> Unit) {
    action(a); action(b); action(c)
}
fun findFirstEven(): Int {
    forEach3(1, 3, 4) { if (it % 2 == 0) return it }   // returns from findFirstEven, not the lambda
    return -1
}

// Value-returning inline fun whose body invokes the lambda param.
inline fun runBlock(block: () -> Int): Int = block()
fun computed(): Int = runBlock { 6 * 7 }

// Mutable-capture: the lambda mutates a `var` of the caller (works because the body is spliced inline).
inline fun repeat3(action: (Int) -> Unit) { action(0); action(1); action(2) }
fun sum(): Int {
    var total = 0
    repeat3 { total = total + it }
    return total
}

fun main() {
    println(findFirstEven())   // 4
    println(computed())        // 42
    println(sum())             // 0+1+2 = 3
}
