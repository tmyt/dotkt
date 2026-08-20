// The BASIC `enum class` DECLARATION lives in a SEPARATE file from its use site (EnumTests.kt) — a
// same-assembly cross-file #90 repro. bir2cir must collect basic-enum names MODULE-WIDE (across every
// .bir.json), not per-file, or `EnumBasic.A.toString()` in EnumTests.kt would not see EnumBasic's
// `kind:"enum"` and would dead-end in ilemit. (Migrated from cases/il-enumtostr/enum.kt.)
import kotlin.clr.ClrEvent
import kotlin.clr.clrEvent

enum class EnumBasic { A, B, C }

// The local event declaration deliberately lives in a sibling source file from its subscriptions in EnumTests.kt.
// This exercises bir2cir's module-wide local-event index, including inherited and type-parameter receiver views.
open class EnumNamedOwnedEvent {
    val pulse: ClrEvent<(Int) -> Unit> by clrEvent()

    fun raise(value: Int) {
        pulse.invoke(value)
    }
}

class EnumDerivedOwnedEvent : EnumNamedOwnedEvent()

open class EnumGenericOwnedEvent<T> {
    val pulse: ClrEvent<(T) -> Unit> by clrEvent()

    fun raise(value: T) {
        pulse.invoke(value)
    }
}

fun <V, T : EnumGenericOwnedEvent<V>> exerciseGenericOwnerConstraint(source: T, value: V): Int {
    var seen = 0
    val subscription = source.pulse.subscribe { if (it == value) seen++ }
    source.raise(value)
    subscription.close()
    source.raise(value)
    return seen
}

fun <T : EnumNamedOwnedEvent> exerciseGenericLocalEvent(source: T): Int {
    var seen = 0
    val subscription = source.pulse.subscribe { seen += it }
    source.raise(8)
    subscription.close()
    source.raise(10)
    return seen
}
