package clr
import kotlin.coroutines.Continuation
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY) annotation class Clr(val name: String)
@Clr("System.Threading.Tasks.Task") class Task<T>
@Clr("DotKt.Coroutines.FlowCol") class FlowCol<T> { @Clr("EmitRaw") fun emitRaw(value: T): Task<Int> = TODO() }
@Clr("DotKt.Coroutines.Flow") class Flow<T>
@Clr("DotKt.Coroutines.GFlows") object Flows {
	// receiver-style block: `suspend FlowCol<T>.() -> Int` (vs the explicit `(FlowCol<T>) -> Int`).
	@Clr("Create") fun <T> flow(block: suspend FlowCol<T>.() -> Int): Flow<T> = TODO()
	@Clr("Collect") fun <T> collectRaw(flow: Flow<T>, action: suspend (T) -> Int): Task<Int> = TODO()
}
@Clr("DotKt.Coroutines.Structured") object Co { @Clr("RunBlockingI") fun runBlocking(block: suspend () -> Int): Int = TODO() }
@Clr("DotKt.Coroutines.Builders") object Bridge { @Clr("OnCompleteInt") fun onComplete(task: Task<Int>, cont: Continuation<Int>) {} }
