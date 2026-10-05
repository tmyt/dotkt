package roundtrip.nullablecompanion

import kotlin.coroutines.Continuation
import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

fun <U, T : U> companionWiden(value: T): U = value
fun <T> companionNullable(value: T): T? = companionWiden<T?, T>(value)
fun <T> companionDeferred(value: T): () -> T? = { companionWiden<T?, T>(value) }

open class CompanionBox<T>(val original: T) {
    fun nullable(): T? = companionWiden<T?, T>(original)
    fun deferred(): () -> T? = { nullable() }
}
class DerivedCompanionBox<T>(value: T) : CompanionBox<T>(value)
fun <T> companionBox(value: T): CompanionBox<T> = DerivedCompanionBox(value)

interface CompanionNamed { val name: String }
class CompanionName(override val name: String) : CompanionNamed
class BoundedCompanionBox<T : CompanionNamed>(val original: T) {
    fun nullable(): T? = companionWiden<T?, T>(original)
}

private var pendingCompanion: Continuation<Unit>? = null
suspend fun <T> companionSuspended(value: T): T? {
    suspendCoroutine<Unit> { pendingCompanion = it }
    return companionWiden<T?, T>(value)
}
fun resumeCompanion() {
    val continuation = pendingCompanion ?: error("missing suspended continuation")
    pendingCompanion = null
    continuation.resume(Unit)
}
