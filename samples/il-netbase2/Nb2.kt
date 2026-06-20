// Override a .NET base class's VIRTUAL member, dispatched polymorphically through the .NET base type.
import System.Exception

open class AppError(val code: Int) : Exception("base msg") {
    override val Message: String get() = "AppError #$code"
}
class FatalError(code: Int) : AppError(code)

// Takes the .NET base type; the override dispatches virtually.
fun describe(e: Exception): String = e.Message

fun main() {
    println(describe(AppError(7)))       // AppError #7
    println(describe(FatalError(21)))    // AppError #21
}
