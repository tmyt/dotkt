// #67: a callable reference to a `suspend` function (`::work`, `d::apply`) is lowered as a `newSuspendLambda` ADAPTER
// — the suspend lambda `{ a -> target(a) }` whose body is a suspendCall to the target. bir2cir's SuspendLambdaLowering
// builds the SuspendLambda state machine; kotc emits only the pure suspend FACTS (the suspend fn-type + suspendCall).
// Was a whole-compile abort (kotc leaked a `kotlin.reflect.KSuspendFunctionN` type token ilemit could not resolve, and
// a plain suspend `newDelegate` had no cold-suspend lowering). The ref is invoked through a `suspend (Int)->Int` param.
import dotkt.support.blockOn

suspend fun work(x: Int): Int = x + 1

class Doubler(val base: Int) {
    suspend fun apply(x: Int): Int = base * x
}

fun runRef(f: suspend (Int) -> Int, arg: Int): Int = blockOn { f(arg) }

fun main() {
    println(runRef(::work, 5))        // 6   (top-level suspend fn ref)
    val d = Doubler(10)
    println(runRef(d::apply, 4))      // 40  (bound member suspend fn ref, receiver captured)
}
