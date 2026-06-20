// Inherit a real .NET base class in the direct-IL backend: base ctor call + SetParent + inherited .NET member.
import System.Exception

class AppError(val code: Int) : Exception("app error")

fun main() {
    val e = AppError(7)
    println(e.Message)   // app error   (inherited System.Exception.Message — a .NET property)
    println(e.code)      // 7           (own field)
}
