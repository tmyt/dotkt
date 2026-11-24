package kotlin.reflect.full

import DotKt.Runtime.CompilerServices.kClassCompanionInstance
import DotKt.Runtime.CompilerServices.kClassIsInterface
import kotlin.reflect.KClass

/** Whether the represented declaration is an interface. CLR aliases sharing one class token report the same kind. */
public val KClass<*>.isInterface: Boolean get() = kClassIsInterface(this)

/** The Kotlin companion singleton, or null when this token does not identify a declaration with a companion. */
public val KClass<*>.companionObjectInstance: Any? get() = kClassCompanionInstance(this)
