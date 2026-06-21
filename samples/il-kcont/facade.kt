package clr

@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
annotation class Clr(val name: String)
@Target(AnnotationTarget.FUNCTION) annotation class ClrAwait
// Opt in to the Continuation-class coroutine form (Path B) instead of the struct/IAsyncStateMachine default.
@Target(AnnotationTarget.FUNCTION) annotation class KCont

@Clr("System.Threading.Tasks.Task")
class Task<T>

@ClrAwait
suspend fun <T> Task<T>.await(): T = TODO()

@Clr("Kfc.Api2")
object Api2 {
	@Clr("Step") fun step(v: Int): Task<Int> = TODO()
	@Clr("Boom") fun boom(v: Int): Task<Int> = TODO()
}

@Clr("Kfc.Coro")
object Coro {
	@Clr("Run") fun run(body: suspend () -> Int): Int = TODO()
}
