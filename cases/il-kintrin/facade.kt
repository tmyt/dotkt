package clr

import kotlin.coroutines.Continuation

@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
annotation class Clr(val name: String)
@Target(AnnotationTarget.FUNCTION) annotation class KCont

@Clr("System.Threading.Tasks.Task")
class Task<T>

@Clr("Kfc.Api2")
object Api2 {
	@Clr("Step") fun step(v: Int): Task<Int> = TODO()
}

// The Kotlin-facing await leaf bridge -> DotKt.Coroutines.Builders.OnCompleteInt(Task<int>, Continuation<int>).
@Clr("DotKt.Coroutines.Builders")
object CoBridge {
	@Clr("OnCompleteInt") fun onComplete(task: Task<Int>, cont: Continuation<Int>) {}
}

@Clr("Kfc.Coro")
object Coro {
	@Clr("Run") fun run(body: suspend () -> Int): Int = TODO()
}
