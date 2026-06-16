package clr

@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
annotation class Clr(val name: String)

@Clr("Kfc.Coro")
object Coro {
	@Clr("Delay") suspend fun delay(ms: Int): Unit = TODO()
	@Clr("FetchValue") suspend fun fetchValue(ms: Int, value: Int): Int = TODO()
	@Clr("Run") fun run(body: suspend () -> Int): Int = TODO()
}
