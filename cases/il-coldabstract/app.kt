// bundle-6 ① BUG 3 (bir2cir SuspendColdLowering) — an ABSTRACT-class suspend member round-trips its full
// vtable shape. Base declares `abstract suspend fun poll()`: bir2cir emits BOTH an abstract cold entry
// `poll$dotkt_suspend(): object` (Virtual|Abstract) AND an abstract Task<Int> bridge `poll` carrying
// [KotlinFunction(Suspend)] (so a re-consuming Kotlin restores `suspend fun`). Impl overrides BOTH slots in
// lockstep (override cold entry + override bridge). The suspend call `b.poll()` (b typed as Base) dispatches
// VIRTUALLY through the abstract cold entry to Impl's override. Drained synchronously by blockOn.
//
// (The INTERFACE half — `interface Fetcher { suspend fun fetch() }` — is exercised by cases/il-ifacesuspend.
// kotc emits an interface `suspend fun` with `virtual:true` but NO `abstract` flag and an empty body; bir2cir
// DERIVES the abstract fact from the enclosing type being an interface, so its cold entry + Task bridge are
// emitted abstract (mirroring this abstract-class shape) — ilverify-clean.)
import dotkt.support.blockOn

abstract class Base { abstract suspend fun poll(): Int }
class Impl(val n: Int) : Base() { override suspend fun poll(): Int = n + 1 }

fun main() {
    val b: Base = Impl(41)
    println(blockOn { b.poll() })   // 42 — virtual dispatch through the abstract cold entry
}
