package clr
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY) annotation class Clr(val name: String)
@Clr("DotKt.Coroutines.Structured") object Co { @Clr("RunBlockingI") fun runBlocking(block: suspend () -> Int): Int = TODO() }
