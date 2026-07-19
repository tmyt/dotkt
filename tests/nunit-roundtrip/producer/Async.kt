// Producer surface part 4: a suspend function consumed cross-module. `suspend` is a Kotlin-only modifier
// (CPS lowering) that rides across as DotKt.Metadata; the consumer drives it through the blockOn harness.
package roundtrip.api

suspend fun asyncDouble(n: Int): Int = n * 2
