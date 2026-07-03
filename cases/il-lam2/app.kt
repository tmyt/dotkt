// Bundle-6 P3 wave-2b — a capturing suspend lambda with a real suspend call. The `{ h() + n }` block
// CAPTURES the outer local `n` (a SuspendLambda ctor param + field) and makes a suspend call to `h()`
// (same-assembly cold entry). kotc emits `suspendLambdaNew` with captures=[n] and a suspendCall-tagged
// body; bir2cir builds the SM (ctor takes n, invokeSuspend calls h's cold entry, resumes with the sum).
import kotlin.clr.blockOn

suspend fun h(): Int = 5

fun main() {
    val n = 10
    println(blockOn { h() + n })   // 15
}
