package clr
import kotlin.coroutines.Continuation
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY) annotation class Clr(val name: String)
@Clr("System.Threading.Tasks.Task") class Task<T>
@Clr("DotKtx.Coroutines.Chan") class Channel<T>(capacity: Int) {
	@Clr("SendAsync") fun sendAsync(v: T): Task<Int> = TODO()
	@Clr("ReceiveAsync") fun receiveAsync(): Task<T> = TODO()
	@Clr("Close") fun close() {}
}
@Clr("DotKtx.Coroutines.Structured") object Co { @Clr("RunBlockingI") fun runBlocking(block: suspend () -> Int): Int = TODO() }
@Clr("DotKt.Coroutines.Builders") object Bridge {
	@Clr("OnCompleteInt") fun onCompleteInt(task: Task<Int>, cont: Continuation<Int>) {}
	@Clr("OnComplete") fun <T> onComplete(task: Task<T>, cont: Continuation<T>) {}
}
