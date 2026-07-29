// An argument that NEVER RETURNS — an expression-position `throw`, here inside the block a lambda-only `run { … }`
// splices — sitting to the LEFT of a nested suspension in a SUSPENDING call's own argument list. Semantically the
// whole call is unreachable: the throw leaves before either suspension. But a suspension POINT is a label, a state
// save and a resume arm, and the cold-call builder has no way to emit none, so this one arrangement is refused with
// the shape and the workaround named. It is the same family as the known nested-suspension-in-an-argument-list
// defect, not a new restriction: the shape never lowered.
//
// The CONTRAST — every neighbouring arrangement compiles and runs, pinned in
// tests/coroutines/fixtures/InlineEvaluationPlanSuspendTests.kt:
//   * the same operand in a NON-suspending call that merely contains a suspension (`sum(run { throw }, relay())`),
//     where the terminal operand simply becomes the expression's value;
//   * a terminal argument with NO suspension to its right (`one(run { throw })`);
//   * a terminal argument with the suspension to its LEFT (`sum(relay(), run { throw })`), which suspends, resumes,
//     and only then leaves.
suspend fun ctaRelay(): Int = 5

suspend fun ctaSum(a: Int, b: Int): Int = a + b

suspend fun ctaTerminalBeforeSuspension(): Int =
    ctaSum(run<Int> { throw IllegalStateException("boom") }, ctaRelay())

suspend fun main() {
    println(ctaTerminalBeforeSuspension())
}
