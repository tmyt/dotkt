// #60 (W1): a CROSS-MODULE inline MEMBER of the klib stdlib (`Duration.toComponents`, a member inline fn with a DISPATCH
// receiver + a lambda arg) called with a NON-LOCAL `return` from inside the lambda. kotc is body-blind here (the klib is
// metadata-only; the [KotlinInline] payload lives on the ref.dll), so it must emit an owner-ful `callInline` and let
// bir2cir splice the body — the non-local `return` must return from the CALLER (`pick`), not from a real delegate.
// Before the fix the call fell to a plain `callInstance` + a REAL delegate and the `return` returned from the delegate,
// so `pick` fell through to `return -1` — a SILENT control-flow miscompile (Unit/T shapes gave no ilverify error).
import kotlin.time.Duration.Companion.seconds

fun pick(): Int {
    val d = 3661.seconds          // 1h 1m 1s
    d.toComponents { hours, minutes, _, _ ->
        if (hours > 0L) return hours.toInt()   // NON-LOCAL return -> must exit pick()
        return minutes
    }
    return -1                     // reached ONLY if the non-local return wrongly exits the delegate
}

fun main() {
    println(pick())               // hours=1 > 0 -> 1 (delegate-return bug would print -1)
}
