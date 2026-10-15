package roundtrip.publishedcallables

import kotlin.clr.ClrField

@PublishedApi
internal fun helper(): Int = 7

inline fun publicEntry(): Int = helper()
inline fun publicSplice(block: (Int) -> Int): Int = block(helper())
fun publicDefault(value: Int = helper()): Int = value

@PublishedApi internal var stored: Int = 3
@PublishedApi internal const val constant: Int = 4
@PublishedApi internal val computed: Int get() = 5
@PublishedApi internal val String.extension: Int get() = length

interface Bound
class BoundValue : Bound

@PublishedApi internal fun <T : Bound> constrained(value: T): T = value
inline fun <T : Bound> publicConstrained(value: T, block: (T) -> T): T = block(constrained(value))

class Holder @PublishedApi internal constructor(val value: Int) {
    @PublishedApi internal var member: Int = 6
    @PublishedApi internal val computedMember: Int get() = member + value
    @PublishedApi internal fun memberHelper(): Int = value
}

class SecondaryHolder() {
    var value: Int = 0
        private set

    @PublishedApi internal constructor(value: Int) : this() { this.value = value }
}

class GenericHolder<T>(val value: T) {
    @PublishedApi internal fun memberHelper(): T = value
    @PublishedApi internal val member: T get() = value
}

inline fun publicProperties(block: (Int) -> Int): Int {
    stored = 8
    val holder = Holder(9)
    holder.member = 10
    return block(stored + constant + computed + "abc".extension +
        holder.member + holder.computedMember + holder.memberHelper() + SecondaryHolder(11).value)
}

inline fun <T> publicGeneric(value: T, block: (T) -> T): T =
    block(GenericHolder(value).memberHelper())

inline fun <T> publicGenericProperty(value: T, block: (T) -> T): T =
    block(GenericHolder(value).member)

@PublishedApi internal suspend fun suspendedHelper(): Int = helper()
suspend fun publicSuspended(): Int = suspendedHelper()

class StaticHost {
    companion {
        @PublishedApi internal fun staticHelper(): Int = helper()
        @PublishedApi internal var staticProperty: Int = 12
    }
}

@PublishedApi internal companion fun StaticHost.extensionHelper(): Int = helper()
@PublishedApi internal companion var StaticHost.extensionProperty: Int = 13

inline fun publicStatics(block: (Int) -> Int): Int {
    StaticHost.staticProperty = 14
    StaticHost.extensionProperty = 15
    return block(StaticHost.staticHelper() + StaticHost.staticProperty +
        StaticHost.extensionHelper() + StaticHost.extensionProperty)
}

open class InheritedVisibilityBase {
    @PublishedApi internal val secret: Int get() = 7
    @PublishedApi @ClrField internal var secretField: Int = 9
    val publicValue: Int get() = 11
    @ClrField var publicField: Int = 12
    @ClrField protected var protectedField: Int = 13
}
