@file:Suppress("INVISIBLE_MEMBER", "INVISIBLE_REFERENCE")
package roundtrip.overloadpriority

class Target {
    val selected: String
    constructor(message: String?, cause: Throwable?) { selected = "constructor" }
    constructor(factory: Boolean) { selected = "factory" }
}
typealias Alias = Target
@kotlin.internal.LowPriorityInOverloadResolution
fun Alias(message: String?, cause: Throwable?): Target = Target(true)
fun sourceAlias(): String = Alias("message", null).selected

fun pick(value: Any?): String = "normal"
@kotlin.internal.LowPriorityInOverloadResolution
fun pick(value: String?): String = "low"
fun sourcePick(): String = pick("value")
fun control(value: Any?): String = "normal"
fun control(value: String?): String = "specific"
fun sourceControl(): String = control("value")

fun <T> genericPick(value: T, argument: Any?): String = "normal"
@kotlin.internal.LowPriorityInOverloadResolution
inline fun <T> genericPick(value: T, argument: String?): String = "low"
fun sourceGeneric(): String = genericPick(7, "value")
fun String.extensionPick(value: Any?): String = "normal"
@kotlin.internal.LowPriorityInOverloadResolution
fun String.extensionPick(value: String?): String = "low"
fun sourceExtension(): String = "receiver".extensionPick("value")

class Members {
    fun memberPick(value: Any?): String = "normal"
    @kotlin.internal.LowPriorityInOverloadResolution
    fun memberPick(value: String?): String = "low"
    infix fun join(value: Any?): String = "normal"
    @kotlin.internal.LowPriorityInOverloadResolution
    infix fun join(value: String?): String = "low"
    operator fun plus(value: Any?): String = "normal"
    @kotlin.internal.LowPriorityInOverloadResolution
    operator fun plus(value: String?): String = "low"
    fun String.memberExtension(value: Any?): String = "normal"
    @kotlin.internal.LowPriorityInOverloadResolution
    fun String.memberExtension(value: String?): String = "low"
}
interface Defaults {
    fun inheritedPick(value: Any?): String = "normal"
    @kotlin.internal.LowPriorityInOverloadResolution
    fun inheritedPick(value: String?): String = "low"
}
class Inherited : Defaults
fun sourceMembers(): String = Members().memberPick("value") + (Members() join "value") +
    (Members() + "value") + with(Members()) { "receiver".memberExtension("value") } +
    Inherited().inheritedPick("value")

class StaticHost {
    companion object {
        fun staticPick(value: Any?): String = "normal"
        @kotlin.internal.LowPriorityInOverloadResolution
        fun staticPick(value: String?): String = "low"
    }
}
class GenericHost<T> {
    companion object {
        fun staticPick(value: Any?): String = "normal"
        @kotlin.internal.LowPriorityInOverloadResolution
        fun staticPick(value: String?): String = "low"
    }
}
companion fun StaticHost.companionPick(value: Any?): String = "normal"
@kotlin.internal.LowPriorityInOverloadResolution
companion fun StaticHost.companionPick(value: String?): String = "low"
companion fun GenericHost.companionPick(value: Any?): String = "normal"
@kotlin.internal.LowPriorityInOverloadResolution
companion fun GenericHost.companionPick(value: String?): String = "low"
fun sourceStatics(): String = StaticHost.staticPick("value") + GenericHost.staticPick("value") +
    StaticHost.companionPick("value") + GenericHost.companionPick("value")

suspend fun suspendedPick(value: Any?): String = "normal"
@kotlin.internal.LowPriorityInOverloadResolution
suspend fun suspendedPick(value: String?): String = "low"
interface SuspendSlot {
    @kotlin.internal.LowPriorityInOverloadResolution
    suspend fun onlyLow(value: String): String
}
class SuspendImplementation : SuspendSlot {
    @kotlin.internal.LowPriorityInOverloadResolution
    override suspend fun onlyLow(value: String): String = value
    suspend operator fun invoke(value: Any?): String = "normal"
    @kotlin.internal.LowPriorityInOverloadResolution
    suspend operator fun invoke(value: String?): String = "low"
}
suspend fun sourceSuspended(): String = suspendedPick("value") + SuspendImplementation()("value")
