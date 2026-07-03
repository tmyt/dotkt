// bundle-6 ③ — the INTERFACE half of the abstract/interface suspend round-trip. An interface `suspend fun`
// member must carry the neutral `"suspend":true`+`resultType` FACT in the BIR (kotc-side) so bir2cir can
// synthesize the Task-bridge signature / cold-entry for the interface member — exactly as it already does for
// an abstract-CLASS suspend member (il-coldabstract). Fetcher declares `suspend fun fetch()`; Fetcher42
// overrides it; the suspend call `f.fetch()` (f typed as Fetcher) dispatches virtually through the interface
// cold entry to the override. Drained synchronously by blockOn.
import kotlin.clr.blockOn

interface Fetcher { suspend fun fetch(): Int }
class Fetcher42(val n: Int) : Fetcher { override suspend fun fetch(): Int = n + 1 }

fun main() {
    val f: Fetcher = Fetcher42(41)
    println(blockOn { f.fetch() })   // 42 — virtual dispatch through the interface cold entry
}
