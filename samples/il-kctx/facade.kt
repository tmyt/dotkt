package clr
import kotlin.coroutines.CoroutineContext
@Target(AnnotationTarget.CLASS, AnnotationTarget.FUNCTION, AnnotationTarget.PROPERTY) annotation class Clr(val name: String)
@Clr("DotKt.Coroutines.Structured") object Co { @Clr("RunBlockingI") fun runBlocking(block: suspend () -> Int): Int = TODO() }
// A runtime Element carrying an Int. @Clr maps it to the runtime type (body not emitted); the supertype + members
// are only for frontend type-checking, like the Continuation facade in T5.
@Clr("DotKt.Coroutines.IntTag") class IntTag(value: Int) : CoroutineContext.Element {
	@Clr("Value") val value: Int get() = TODO()
	override val key: CoroutineContext.Key<*> get() = TODO()
	override fun <E : CoroutineContext.Element> get(key: CoroutineContext.Key<E>): E? = TODO()
	override fun <R> fold(initial: R, operation: (R, CoroutineContext.Element) -> R): R = TODO()
	override fun plus(context: CoroutineContext): CoroutineContext = TODO()
	override fun minusKey(key: CoroutineContext.Key<*>): CoroutineContext = TODO()
}
@Clr("DotKt.Coroutines.Tags") object Tags { @Clr("TagKey") fun tagKey(): CoroutineContext.Key<IntTag> = TODO() }
