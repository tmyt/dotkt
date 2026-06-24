package clr
import kotlin.coroutines.Continuation
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY) annotation class Clr(val name: String)
@Clr("System.Threading.Tasks.Task") class Task<T>
@Clr("System.Collections.Generic.IAsyncEnumerable") class AsyncSeq<T>
@Clr("DotKtx.Coroutines.FlowCol") class FlowCol<T> { @Clr("EmitRaw") fun emitRaw(value: T): Task<Int> = TODO() }
@Clr("DotKtx.Coroutines.Flow") class Flow<T>
@Clr("Kfc.Api") object Api { @Clr("Range") fun range(n: Int): AsyncSeq<Int> = TODO() }
@Clr("DotKtx.Coroutines.GFlows") object Flows {
	@Clr("FromAsync") fun <T> fromAsync(src: AsyncSeq<T>): Flow<T> = TODO()
	@Clr("Collect") fun <T> collectRaw(flow: Flow<T>, action: suspend (T) -> Int): Task<Int> = TODO()
}
@Clr("DotKtx.Coroutines.Structured") object Co { @Clr("RunBlockingI") fun runBlocking(block: suspend () -> Int): Int = TODO() }
@Clr("DotKt.Coroutines.Builders") object Bridge { @Clr("OnComplete") fun <T> onComplete(task: Task<T>, cont: Continuation<T>) {} }
