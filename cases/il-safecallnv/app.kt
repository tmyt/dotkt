// A5 regression gate: `a?.member` where the RESULT is a nullable value type.
// (1) The receiver must be evaluated exactly ONCE (the old emitter spliced the rendered receiver into
//     both the null check and the member access -> two evaluations).
// (2) A nullable-VALUE-type receiver (`Char?`) must be HasValue-gated and UNWRAPPED (.Value) before the
//     member/conversion — splicing the raw Nullable<char> under `conv int` was invalid IL
//     (System.InvalidProgramException at run).
var m = 0

fun g(): Char? {
    m++
    return 'x'
}

fun gn(): Char? {
    m++
    // null routed through a cond: EmitCond coerces the T/null branches to Nullable<T>. A bare `return null`
    // for a nullable-VALUE return is a separate, pre-existing formal-verification gap (Nullobjref where
    // Nullable`1<char> is expected) outside this case's scope.
    return if (m < 0) 'x' else null
}

fun s(): String? {
    m++
    return "hey"
}

fun sn(): String? {
    m++
    return null
}

fun main() {
    println(g()?.code)    // 120 — nullable VALUE receiver, unwrapped
    println(gn()?.code)   // null path
    println(s()?.length)  // 3 — reference receiver, value-type result
    println(sn()?.length) // null path
    println(m)            // 4 — every receiver ran exactly once
}
