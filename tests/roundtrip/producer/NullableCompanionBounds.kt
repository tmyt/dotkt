package roundtrip.nullablecompanion

import kotlin.coroutines.Continuation
import kotlin.coroutines.resume
import kotlin.coroutines.suspendCoroutine

fun <U, T : U> companionWiden(value: T): U = value
fun <T> companionNullable(value: T): T? = companionWiden<T?, T>(value)
fun <T> companionDeferred(value: T): () -> T? = { companionWiden<T?, T>(value) }

fun <T> capturedCompanion(value: T): suspend () -> T? = {
    companionSuspended(value)
    companionWiden<T?, T>(value)
}

interface VariantCompanion<out T> { fun get(): T? }
class VariantCompanionImpl<T>(val value: T) : VariantCompanion<T> {
    override fun get(): T? = companionWiden<T?, T>(value)
}

class OuterCompanion<T>(val value: T) {
    inner class Inner<U>(val other: U) {
        fun outer(): T? = companionWiden<T?, T>(value)
        fun inner(): U? = companionWiden<U?, U>(other)
    }
}

open class VirtualCompanion {
    open fun <T> get(value: T): T? = companionWiden<T?, T>(value)
}

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

interface CompanionSink { fun <T> count(values: List<T?>): Int }
open class CompanionSinkBase { fun <T> count(values: List<T?>): Int = values.size }
class LocalCompanionSink : CompanionSinkBase(), CompanionSink

interface BoundedCompanionSink { fun <T : CompanionNamed> count(values: List<T?>): Int }
open class BoundedCompanionSinkBase {
    fun <T : CompanionNamed> count(values: List<T?>): Int = values.size
}
class LocalBoundedCompanionSink : BoundedCompanionSinkBase(), BoundedCompanionSink

interface DefaultCompanionSink { fun <T> count(values: List<T?>): Int = values.size }
class LocalDefaultCompanionSink : DefaultCompanionSink
