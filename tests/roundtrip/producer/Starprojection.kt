// A bounded generic star projection has no direct invariant CLR representation. bir2cir emits a non-generic
// existential interface for StarKey<*>, while [KotlinType] must restore the original projection when this DLL is
// consumed as Kotlin. Without the carrier, StarOwner.key and isConcreteStarKey's parameter re-import as Any?.
package starprojection

import kotlin.coroutines.Continuation
import kotlin.clr.ClrName

interface StarElement
interface StarKey<E : StarElement>

class ConcreteStarElement : StarElement
object ConcreteStarKey : StarKey<ConcreteStarElement>

interface StarOwner {
    val key: StarKey<*>
}

private class StarOwnerImpl : StarOwner {
    override val key: StarKey<*> get() = ConcreteStarKey
}

fun starOwner(): StarOwner = StarOwnerImpl()
fun isConcreteStarKey(key: StarKey<*>): Boolean = key === ConcreteStarKey

// The producer deliberately never star-projects this declaration. A downstream module is nevertheless free to
// consume the exported generic classifier as FirstUseBox<*>, so its existential CLR ABI cannot depend on whether this
// source module happened to use that projection itself.
interface FirstUseBox<T> {
    fun get(): T
}

private class FirstUseStringBox : FirstUseBox<String> {
    override fun get(): String = "first-use"
}

fun firstUseBox(): FirstUseBox<String> = FirstUseStringBox()

interface MixedBox<A, B> {
    fun first(): A
    fun second(): B
    fun choose(value: A): String
    fun choose(value: String): String
}

private class MixedValueBox : MixedBox<Int, String> {
    override fun first(): Int = 23
    override fun second(): String = "mixed"
    override fun choose(value: Int): String = "int:$value"
    override fun choose(value: String): String = "string:$value"
}

fun mixedBox(): MixedBox<*, String> = MixedValueBox()

// A selected declaration's allocated physical name may equal a sibling's source name. Existential binding must follow
// the declaration identity back to `chosen`, rather than treating the rewritten `other` spelling as source identity.
interface ExplicitNameStarCollision<T> {
    @Suppress("INAPPLICABLE_JVM_NAME")
    @ClrName("other")
    fun chosen(value: Int): T = other("chosen:$value")
    fun other(value: String): T
}

private class ExplicitNameStarCollisionImpl : ExplicitNameStarCollision<String> {
    override fun other(value: String): String = value
}

fun explicitNameStarCollision(): ExplicitNameStarCollision<String> = ExplicitNameStarCollisionImpl()

interface OverloadedStarSink<T> {
    fun accept(value: Int): T
    fun accept(value: String): T
}

private class OverloadedStarSinkImpl : OverloadedStarSink<String> {
    override fun accept(value: Int): String = "int:$value"
    override fun accept(value: String): String = "string:$value"
}

fun overloadedStarSink(): OverloadedStarSink<*> = OverloadedStarSinkImpl()

interface CollisionHost<T> {
    // Keep source classifiers adjacent to every word in the compiler's preferred physical suffix. Kotlin source
    // cannot spell '$' even in an escaped identifier, so the allocator's exact reserved spelling is unspeakable;
    // association must nevertheless come only from metadata, never a fuzzy name match.
    class dotkt {
        class star
    }

    fun value(): T
}

private class CollisionHostImpl : CollisionHost<String> {
    override fun value(): String = "collision-safe"
}

fun collisionHost(): CollisionHost<*> = CollisionHostImpl()

// A derived star receiver cannot be cast to one arbitrary invariant ReferencedInnerBase<ReferencedOuter<object>>
// merely to satisfy an inner constructor's hidden outer slot. The producer publishes the existential factory ABI;
// the consumer below sees only this emitted assembly/KLIB and selects the exact overload from trusted metadata.
open class ReferencedInnerBase<T>(private val outer: T) {
    inner class Entry {
        private val value: String
        constructor(value: Int) { this.value = "i$value" }
        constructor(value: String) { this.value = "s$value" }
        fun render(): String = outer.toString() + ":" + value
    }
    inner class GenericEntry<E>(private val value: E) {
        fun render(): String = outer.toString() + ":g" + value.toString()
    }
    inner class DefaultEntry(private val value: String = "default") {
        fun render(): String = outer.toString() + ":" + value
    }
}

class ReferencedOuter<E>(private val label: String) {
    override fun toString(): String = label
}

class ReferencedInnerLeaf<E>(outer: ReferencedOuter<E>) :
    ReferencedInnerBase<ReferencedOuter<E>>(outer)

fun referencedInnerLeaf(label: String): ReferencedInnerLeaf<String> =
    ReferencedInnerLeaf(ReferencedOuter<String>(label))

class ReferencedOwnerPair<A, B>
open class ReferencedConstrainedInnerBase<T>(private val label: String) {
    fun <E : T?> nullableMethodBound(value: E): String =
        label + ":method:" + (value?.toString() ?: "null")

    inner class Token<E : T>(private val value: E?) {
        fun render(): String = label + ":" + (value?.toString() ?: "null")
    }
    inner class PairToken<E, F>(private val first: E?, private val second: F?) where E : T, F : T {
        fun render(): String = label + ":" + (first?.toString() ?: "null") + ":" + (second?.toString() ?: "null")
    }
    inner class MixedToken<E : T, F>(private val first: E?, private val second: F) {
        fun render(): String = label + ":" + (first?.toString() ?: "null") + ":" + second.toString()
    }
    inner class NestedToken<F, E>(private val value: E?) where E : ReferencedOwnerPair<T, F> {
        fun render(): String = label + ":" + (value?.toString() ?: "null")
    }
    inner class NullableToken<E : T?>(private val value: E) {
        fun render(): String = label + ":" + (value?.toString() ?: "null")
    }
    inner class TransitiveToken<E, F>(private val value: E?) where E : T, F : List<E>
}

class ReferencedConstrainedInnerLeaf<T>(label: String) : ReferencedConstrainedInnerBase<T>(label)
fun referencedConstrainedInnerLeaf(label: String): ReferencedConstrainedInnerLeaf<String> =
    ReferencedConstrainedInnerLeaf(label)

fun <T, E : T?> referencedNullableMethodBound(value: E): String =
    value?.toString() ?: "method-null"

suspend fun <T, E : T?> referencedNullableSuspendMethodBound(value: E): Int =
    if (value == null) 1 else 2

open class ReferencedCapturedNullableOuter<T, U : T?> {
    inner class Token<E>(private val value: E) {
        fun render(): String = value.toString()
    }
}

interface ReferencedNullableMethodContract<T> {
    fun <E : T?> inheritedNullableMethodBound(value: E): String
}

class ReferencedNullableMethodImplementation<T> : ReferencedNullableMethodContract<T> {
    override fun <E : T?> inheritedNullableMethodBound(value: E): String =
        value?.toString() ?: "inherited-method-null"
}

// The inline payload is consumed from the producer DLL. Its star-projected receiver must bind to the exact
// existential slot again after the body is spliced into a downstream module.
inline fun deliverStarContinuationFailure(
    continuation: Continuation<*>,
    failure: Throwable,
    beforeResume: () -> Unit,
) {
    beforeResume()
    continuation.resumeWith(Result.failure(failure))
}

interface ReferencedStarBase<T> {
    fun inherited(): T
}
