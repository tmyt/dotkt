// #75 S4a §8.7 — arm (c): a SUSPEND call inside a NON-suspend-typed inline-arg lambda. `1.let { tick() }` holds a
// `tick()` suspend call while the `let` lambda is NOT suspend-typed — legal ONLY because inline expansion puts the
// call in work()'s suspend frame. arm (c) => callNeedsSplice true => the owner-less callInline + engine splice drops
// let's body into work's state machine (the delegate path would trap the await in a non-suspend closure = a
// miscompile). The `for` loop is an ordinary loop-with-suspension (already supported) showing the arm-c splice
// composes with surrounding suspension points.
import System.Threading.Tasks.Task
import kotlin.clr.await
import dotkt.support.blockOn

suspend fun tick() { Task.Delay(1).await() }   // a real .NET-async suspension point

suspend fun work(): Int {
    var n = 0
    for (i in 1..2) {
        tick()
        n += 10
    }
    1.let {
        tick()
        n += it
    }
    return n
}

fun main() {
    println(blockOn { work() })   // 10 + 10 + 1 = 21
}
