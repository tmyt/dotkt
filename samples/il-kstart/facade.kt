package clr
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY) annotation class Clr(val name: String)
@Clr("System.Threading.Tasks.Task") class Task<T>
@Clr("Kfc.Api2") object Api2 { @Clr("Step") fun step(v: Int): Task<Int> = TODO() }
@Clr("DotKt.Coroutines.Builders") object Bridge { @Clr("OnCompleteInt") fun onComplete(task: Task<Int>, cont: Continuation<Int>) {} }
// The completion: a .NET Continuation<Int> (the runtime DotKt.Coroutines.CaptureI). The facade only declares the
// supertype + members for frontend type-checking; @Clr maps it to the runtime type, so this body is never emitted.
@Clr("DotKt.Coroutines.CaptureI") class CaptureI : Continuation<Int> {
	@Clr("Await") fun await(): Int = TODO()
	override val context: CoroutineContext get() = TODO()
	override fun resumeWith(result: Result<Int>) = TODO()
}
