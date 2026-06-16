package clr

@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY)
annotation class Clr(val name: String)

@Clr("Kfc.IntTask")
class IntTask {
	@Clr("Value") val value: Int get() = TODO()
}

@Clr("Kfc.Coro")
object Coro {
	@Clr("DelayThenValue") fun delayThenValue(ms: Int, value: Int): IntTask = TODO()
	@Clr("Run") fun run(body: suspend () -> Int): Int = TODO()
}
