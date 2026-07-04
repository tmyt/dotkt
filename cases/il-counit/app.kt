// A PUBLIC (non-main) Unit-returning suspend fun -> a NON-generic public `Task` bridge, per the coroutine ABI
// (coroutine-abi.md §1: `suspend fun f(): Unit` maps to `Task`, NOT `Task<Unit>`; the C#-idiomatic async-void
// shape). bir2cir (SuspendColdLowering.BuildBridge) emits `greet` as a public `Task greet()`: the cold entry
// drives the body, and the returned TaskCompletionSource<Unit>.Task (a Task<Unit>) upcasts to the non-generic
// Task on return (Task<T> : Task). `greet` genuinely suspends on `step()` (completes synchronously here) so the
// full state-machine + Unit Task-bridge emit is exercised and ilverify-checked. `main` drives greet via its
// cold `$dotkt_suspend` entry; the emitted non-generic Task bridge sits on the public surface.
suspend fun step(): Int = 21

suspend fun greet(): Unit {
    val x = step()
    println("hello " + (x * 2))
}

suspend fun main() {
    greet()
    println("done")
}
