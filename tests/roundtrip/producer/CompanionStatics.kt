// Producer half of the Kotlin 2.4 static-declaration round trip (#382). The consumer reads this through the BUILT
// assembly's projected KLIB, never through this source, so every declaration here is exercised exactly as a second
// module rediscovers it from metadata.
package roundtrip.companionstatics

import kotlin.concurrent.Volatile
import kotlin.clr.ClrField

const val TOP_TAG: String = "top-level-counter"
private const val PRIVATE_TOP_TAG: String = "private-top-level-counter"
internal const val INTERNAL_TOP_TAG: String = "internal-top-level-counter"
// Deliberately owns the same accessor name as Box.count. A referenced Box.count must bind through Box's trusted
// generic-static carrier before ownerless top-level get_count/set_count lookup gets first refusal.
val count: Int get() = 99

object NamedConstants {
    const val NAME: String = "named-object-const"
}

class Counter(val n: Int) {
    companion {
        fun twice(x: Int): Int = x * 2
        fun twice(s: String): String = s + s
        val origin: Counter = Counter(0)
        var seen: Int = 1
        const val TAG: String = "counter"
        lateinit var later: String
        @Volatile lateinit var volatileLater: String
        fun readVolatileLater(): String = volatileLater
        suspend fun suspendedTwice(x: Int): Int = x * 2
    }

    // A real companion object on the SAME class: it must stay a distinct singleton across the round trip.
    companion object {
        const val OBJECT_TAG: String = "real-companion-const"
        val label: String = "real-companion"
        fun describe(): String = "obj:" + label
    }

    fun bump(): Int = n + 1
}

fun localCompanionSuspendReference(): suspend (Int) -> Int = Counter::suspendedTwice

// Deliberately collides with the class static above. A consumer's Counter::suspendedTwice reference must retain its
// projected declaring type instead of being captured by the ownerless top-level name/signature index.
suspend fun suspendedTwice(x: Int): Int = x * 100

class Box<T>(val v: T) {
    val collidingAccessor: Int get() = 7

    companion {
        private val secret: Int = 5
        private fun hidden(): Int = 6
        fun make(): String = "box"
        fun lambdaFactory(): () -> Int = { 42 }
        fun capturingLambdaFactory(seed: Int): () -> Int = { seed + 1 }
        fun __lambda0(): Int = 7
        fun localClassValue(): Int {
            class Local { fun value(): Int = 43 }
            return Local().value()
        }
        // This is deliberately the CLR accessor name of the unrelated instance property above. The property row
        // must follow its own Kotlin-static fact, not whichever method happens to share its physical name.
        fun get_collidingAccessor(): Int = 9
        fun <R> echoNullable(value: R?): R? = value
        inline fun runInline(block: () -> Int): Int = block() + 1
        fun withDefault(value: String = "box-default"): String = value
        var count: Int = 0
        @Volatile @ClrField private var volatileSecret: Int = 7
        const val CODE: String = "generic-box"
        const val NAN: Double = Double.NaN
        const val BIG: UInt = 4_000_000_000u
        lateinit var later: String
        suspend fun suspendedMake(value: Int): Int = value + 3
    }

    fun revealPrivateStatics(): Int = secret + hidden()
    fun readPrivateVolatile(): Int = volatileSecret
    fun writePrivateVolatile(value: Int) { volatileSecret = value }
}

fun localGenericCompanionInline(): Int = Box.runInline { 20 }

class GenericOuter<T> {
    class Nested<U> {
        companion { fun label(): String = "nested-generic" }
    }
}

var constrainedBoxInitializations: Int = 0
private fun initializeConstrainedBox(): Int {
    constrainedBoxInitializations += 1
    return 17
}

interface ConstrainedBoxValue
class FirstConstrainedBoxValue : ConstrainedBoxValue
class SecondConstrainedBoxValue : ConstrainedBoxValue

class ConstrainedBox<T : ConstrainedBoxValue>(val v: T) {
    companion {
        val token: Int = initializeConstrainedBox()
        fun label(): String = "constrained"
    }
}

class FBoundedBox<T : Comparable<T>> {
    companion {
        private fun hidden(): Int = 19
        fun value(): Int = hidden()
    }
}

interface Shape {
    fun area(): Int
    companion {
        fun unitArea(): Int = 1
        val kind: String get() = "shape"
    }
}

interface GenericShape<T> {
    companion {
        fun unitArea(): Int = 2
        val kind: String get() = "generic-shape"
    }
}

class Tag(val label: String)
class OtherTag(val label: String)
class TagContext(val prefix: String)
class MutableTagContext(var value: Int)
class ReadOnlyTagContext(var value: Int)
class GenericTag<T>
typealias StringGenericTag = GenericTag<String>

// Companion EXTENSIONS: receiverless statics associated with `Tag`, physically hosted by this file's facade class.
companion fun Tag.of(label: String): Tag = Tag(label)
companion fun Tag.of(value: Int): Tag = Tag("n:$value")
companion fun <T> Tag.keep(value: T): T = value
companion fun Tag.formatTag(prefix: String = "tag", value: String): String = "$prefix:$value"
companion suspend fun Tag.suspended(label: String): Tag = Tag(label)
companion val Tag.blank: Tag get() = Tag("")
companion val Tag.marker: String = "m"
companion var Tag.counter: Int = 0
companion lateinit var Tag.later: String
companion inline fun Tag.withValue(block: () -> Int): Int = block()
inline fun companionExtensionInlineDefault(
    value: Int = Tag.withValue { 37 },
    block: (Int) -> Int,
): Int = block(value)
context(context: TagContext)
companion val Tag.contextLabel: String get() = context.prefix
// Same receiver/name and physical accessor arity, but distinct context types. dll2klib must pair the setter only
// with the getter whose context signature matches; the ReadOnlyTagContext overload remains a val.
context(context: MutableTagContext)
companion var Tag.contextState: Int
    get() = context.value
    set(value) { context.value = value }
context(context: ReadOnlyTagContext)
companion val Tag.contextState: Int get() = context.value
companion fun GenericTag.genericValue(): String = "generic"
companion fun StringGenericTag.aliasValue(): String = "alias"
fun localGenericCompanionExtensionValue(): String = GenericTag.genericValue() + "/" + GenericTag.aliasValue()
companion fun OtherTag.of(label: String): OtherTag = OtherTag(label)
companion fun <T> OtherTag.keep(value: T): T = value
companion val OtherTag.blank: OtherTag get() = OtherTag("other")
companion val OtherTag.marker: String = "other-m"
companion var OtherTag.counter: Int = 10

// The payload is serialized before bir2cir chooses companion-extension physical names, then materialized in the
// consumer. Both the field and function use must be rebound after the default BIR is spliced.
fun companionExtensionDefaults(label: String = Tag.marker, made: Tag = Tag.of("default")): String =
    "$label:${made.label}"

fun of(label: String): String = "top:$label"
val marker: String = "top-m"
