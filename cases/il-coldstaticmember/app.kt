// R1 M3 (#90) — a STATIC/companion suspend member's cold-entry DECLARATION. kotc promotes `companion object {
// suspend fun compute() }` to a STATIC method on the outer class `Calc`; under R1 that static member enters the
// classifier and gets a STATIC cold entry `compute$dotkt_suspend` + a static Task<Int> bridge + a top-level-shaped
// SM (no `$this`) — emitted and ilverify-verified here. The old code REJECTED static members entirely (they kept
// suspend:true -> ilemit ICE); R1's unconditional declaration closes that gap.
//
// The RUNTIME drive uses an `object` member (`Ticker.tick`), which kotc tags as a suspend call. A same-assembly
// *call* to a companion suspend member is currently untagged by kotc (it emits a plain `callStatic owner=Calc`
// with no `suspendCall` fact), so the companion cold entry is exercised as an emitted+verified DECLARATION rather
// than a runtime call — the call-side fact is a kotc concern, reported separately for the coordinator.
import dotkt.support.blockOn

suspend fun bump(x: Int): Int = x + 1

class Calc {
    companion object {
        // A companion suspend member -> a STATIC cold entry on Calc (M3). Its suspend call to the top-level `bump`
        // drives the static SM. Emitted + ilverify-verified even though the call-side kotc gap defers its runtime use.
        suspend fun compute(): Int = bump(41)
    }
}

object Ticker {
    // An object-instance suspend member (kotc tags the call) — drives the runtime assertion over the same cold core.
    suspend fun tick(): Int = bump(41)
}

fun main() {
    println(blockOn { Ticker.tick() })   // 42
}
