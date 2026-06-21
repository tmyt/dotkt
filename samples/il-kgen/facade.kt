package clr
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY) annotation class Clr(val name: String)
@Target(AnnotationTarget.FUNCTION) annotation class ClrAwait
@Clr("System.Threading.Tasks.Task") class Task<T>
@ClrAwait suspend fun <T> Task<T>.await(): T = TODO()
@Clr("Kfc.Api2") object Api2 { @Clr("Step") fun step(v: Int): Task<Int> = TODO(); @Clr("Word") fun word(s: String): Task<String> = TODO() }
@Clr("Kfc.Coro") object Coro { @Clr("Run") fun run(body: suspend () -> Int): Int = TODO(); @Clr("RunS") fun runS(body: suspend () -> String): String = TODO() }
