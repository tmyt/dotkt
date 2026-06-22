package clr
import kotlin.coroutines.Continuation
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY) annotation class Clr(val name: String)
@Target(AnnotationTarget.FUNCTION) annotation class KCont

@Clr("System.Threading.Tasks.Task") class Task<T>

@Clr("DotKtx.Coroutines.FlowColI") class FlowColI { @Clr("EmitRaw") fun emitRaw(value: Int): Task<Int> = TODO() }
@Clr("DotKtx.Coroutines.FlowI") class FlowI
@Clr("DotKtx.Coroutines.Flows") object Flows {
	@Clr("CreateI") fun flow(block: suspend (FlowColI) -> Int): FlowI = TODO()
	@Clr("CollectI") fun collectRaw(flow: FlowI, action: suspend (Int) -> Int): Task<Int> = TODO()
}
@Clr("DotKtx.Coroutines.Structured") object Co {
	@Clr("RunBlockingI") fun runBlocking(block: suspend () -> Int): Int = TODO()
}
@Clr("DotKt.Coroutines.Builders") object Bridge {
	@Clr("OnCompleteInt") fun onComplete(task: Task<Int>, cont: Continuation<Int>) {}
}
