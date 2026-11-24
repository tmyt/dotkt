package kotlin.reflect.full

import DotKt.Runtime.CompilerServices.kClassCompanionInstance
import DotKt.Runtime.CompilerServices.kClassIsInterface
import kotlin.reflect.KClass

/** Whether the represented declaration is an interface, not whether its erased storage is one. */
public val KClass<*>.isInterface: Boolean get() = kClassIsInterface(this)

/** The singleton of the represented declaration's Kotlin companion object, or null if it has none. */
public val KClass<*>.companionObjectInstance: Any? get() = kClassCompanionInstance(this)
