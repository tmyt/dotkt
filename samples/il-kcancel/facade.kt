package clr
import kotlin.coroutines.Continuation
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY) annotation class Clr(val name: String)
@Clr("System.Threading.Tasks.Task") class Task<T>
@Clr("Kfc.Api2") object Api2 { @Clr("Step") fun step(v: Int): Task<Int> = TODO() }
@Clr("Kfc.Coro") object Coro { @Clr("Run") fun run(body: suspend () -> Int): Int = TODO() }
@Clr("DotKt.Coroutines.Builders") object Bridge { @Clr("OnCompleteInt") fun onComplete(task: Task<Int>, cont: Continuation<Int>) {} }
