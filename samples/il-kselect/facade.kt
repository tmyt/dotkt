package clr
import kotlin.coroutines.Continuation
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY) annotation class Clr(val name: String)
@Clr("System.Threading.Tasks.Task") class Task<T>
@Clr("Kfc.Api") object Api { @Clr("Delayed") fun delayed(value: Int, ms: Int): Task<Int> = TODO() }
@Clr("DotKt.Coroutines.Selector") class Selector<R> {
	@Clr("OnAwait") fun <T> onAwait(task: Task<T>, handler: suspend (T) -> R) {}
}
@Clr("DotKt.Coroutines.Selectors") object Sel {
	@Clr("Select") fun <R> selectAsync(block: Selector<R>.() -> Int): Task<R> = TODO()
}
@Clr("DotKt.Coroutines.Structured") object Co { @Clr("RunBlockingI") fun runBlocking(block: suspend () -> Int): Int = TODO() }
@Clr("DotKt.Coroutines.Builders") object Bridge { @Clr("OnComplete") fun <T> onComplete(task: Task<T>, cont: Continuation<T>) {} }
