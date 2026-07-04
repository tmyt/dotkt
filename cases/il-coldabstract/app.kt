// bundle-6 ① BUG 3 (bir2cir SuspendColdLowering) — an ABSTRACT-class suspend member round-trips its full
// vtable shape. Base declares `abstract suspend fun poll()`: bir2cir emits BOTH an abstract cold entry
// `poll$dotkt_suspend(): object` (Virtual|Abstract) AND an abstract Task<Int> bridge `poll` carrying
// [KotlinFunction(Suspend)] (so a re-consuming Kotlin restores `suspend fun`). Impl overrides BOTH slots in
// lockstep (override cold entry + override bridge). The suspend call `b.poll()` (b typed as Base) dispatches
// VIRTUALLY through the abstract cold entry to Impl's override. Drained synchronously by blockOn.
//
// (The INTERFACE half — `interface Fetcher { suspend fun fetch() }` — remains blocked on a kotc gap: the
// interface member is emitted without the `suspend`/`abstract`/`override` flags, so bir2cir cannot recognize
// it; the fix is kotc-side [BirEmitter.kt:3524 ".NET-member generic branch missing suspend tag"].)
import dotkt.support.blockOn

abstract class Base { abstract suspend fun poll(): Int }
class Impl(val n: Int) : Base() { override suspend fun poll(): Int = n + 1 }

fun main() {
    val b: Base = Impl(41)
    println(blockOn { b.poll() })   // 42 — virtual dispatch through the abstract cold entry
}
