package clr
import kotlin.coroutines.Continuation
import kotlin.coroutines.CoroutineContext
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY) annotation class Clr(val name: String)
// A Continuation<Int> whose context carries a RecordingDispatcher (runtime). @Clr maps it; the supertype/members
// are frontend stubs.
@Clr("DotKt.Coroutines.SinkI") class SinkI : Continuation<Int> {
	@Clr("Value") val value: Int get() = TODO()
	override val context: CoroutineContext get() = TODO()
	override fun resumeWith(result: Result<Int>) = TODO()
}
@Clr("DotKt.Coroutines.Recorder") object Recorder { @Clr("Count") fun count(): Int = TODO() }
