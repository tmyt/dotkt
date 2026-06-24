package clr

@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
annotation class Clr(val name: String)

@Target(AnnotationTarget.FUNCTION)
annotation class ClrAwait

@Clr("System.Threading.Tasks.Task")
class Task<T>

// THE generic interop point: await any .NET Task<T> from a Kotlin suspend function.
@ClrAwait
suspend fun <T> Task<T>.await(): T = TODO()

@Clr("Kfc.Api")
object Api {
	@Clr("FetchAsync") fun fetchAsync(ms: Int, value: Int): Task<Int> = TODO()
	@Clr("FailAsync") fun failAsync(): Task<Int> = TODO()
}

@Clr("Kfc.Coro")
object Coro {
	@Clr("Run") fun run(body: suspend () -> Int): Int = TODO()
}
