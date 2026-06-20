// I3 — inherit a REAL .NET base type from Kotlin, façade-free. `clrgen.Exception` is the actual
// System.Exception, injected into FIR (no façade .kt). A Kotlin class extends it, calls the base
// constructor, and OVERRIDES the virtual .NET property Message; calls dispatch virtually through
// the .NET base type. This is the mechanism that unlocks framework-direct UI (class App : Application()).
import System.Exception
import System.Console

open class AppError(val code: Int) : Exception("app error") {
	override val Message: String get() = "AppError #$code: ${code * 2}"
}

class FatalError(code: Int) : AppError(code)

// Takes the .NET base type; subclasses pass polymorphically and dispatch to the override.
fun describe(e: Exception): String = e.Message

fun main() {
	Console.WriteLine(describe(AppError(7)))
	Console.WriteLine(describe(FatalError(21)))
}
