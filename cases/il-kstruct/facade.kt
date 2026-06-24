package clr
import kotlin.coroutines.Continuation
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY) annotation class Clr(val name: String)
@Target(AnnotationTarget.FUNCTION) annotation class KCont
@Clr("System.Threading.Tasks.Task") class Task<T>
@Clr("DotKtx.Coroutines.DeferredI") class DeferredI { @Clr("Task") val task: Task<Int> = TODO() }
@Clr("DotKtx.Coroutines.Structured") object Co {
	@Clr("AsyncI") fun async(block: suspend () -> Int): DeferredI = TODO()
	@Clr("RunBlockingI") fun runBlocking(block: suspend () -> Int): Int = TODO()
}
@Clr("DotKt.Coroutines.Builders") object Bridge {
	@Clr("OnCompleteInt") fun onComplete(task: Task<Int>, cont: Continuation<Int>) {}
}
@Clr("Kfc.Api") object Api { @Clr("Fetch") fun fetch(ms: Int, v: Int): Task<Int> = TODO() }
