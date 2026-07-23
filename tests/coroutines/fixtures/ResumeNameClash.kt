// Regression: a same-assembly top-level function with the same simple name must not suppress file-class
// attribution for the imported kotlin.coroutines.resume extension in ContinuationBridgeTests.kt.
package unrelated.resumeclash

fun String.resume(): String = this
