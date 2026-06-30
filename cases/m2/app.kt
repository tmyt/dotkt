// .NET interop via facadegen: `import System.X` injects a real BCL type façade-free (no hand-written @Clr facade).
// System.Math is a static class -> a Kotlin `object`; its static methods resolve as direct .NET calls.
import System.Math

fun main() {
	println("max(3, 7) = ${Math.Max(3, 7)}")
	println("min(3, 7) = ${Math.Min(3, 7)}")
	println("abs(-9) = ${Math.Abs(-9)}")
}
